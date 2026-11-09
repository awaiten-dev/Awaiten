using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A <c>[Module]</c> another assembly can name compiles a public handle for each service it registers under an
///     internal type that something a consumer reaches needs, so a container in another assembly depends on the handle
///     instead of the service it cannot name.
/// </summary>
public class ModuleHiddenServiceTests
{
	private static uint Fnv1a(string text)
	{
		uint hash = 2166136261;
		foreach (char character in text)
		{
			hash = unchecked((hash ^ character) * 16777619);
		}

		return hash;
	}

	private static string SimpleName(string fullyQualifiedName)
		=> fullyQualifiedName[(fullyQualifiedName.LastIndexOf('.') + 1)..];

	private static string Handle(string service)
		=> $"global::Lib.CoffeeModule.Internals.Hidden_{SimpleName(service)}_{Fnv1a(service):x8}";

	private static string Instance(string implementation)
		=> $"global::Lib.CoffeeModule.Internals.HiddenInstance_{SimpleName(implementation)}_{Fnv1a(implementation):x8}";

	private static string HiddenFactory(string implementation)
		=> $"HiddenRegistration_{SimpleName(implementation)}_{Fnv1a(implementation):x8}";

	private static string Factory(string implementation)
		=> $"Registration_{SimpleName(implementation)}_{Fnv1a(implementation):x8}";

	private static string Alias(string implementation, string service)
		=> $"Alias_{SimpleName(implementation)}_{Fnv1a($"{implementation}\0{service}"):x8}";

	private static string ModuleSource(GeneratorResult result)
		=> result.Sources.Single(source => source.Key.Contains("ModuleScan")).Value;

	private static GeneratorResult RunLibrary(string library) => Generator.Run(library);

	[Test]
	public async Task PublicRegistration_TakesTheInternalServiceThroughItsHandle()
	{
		GeneratorResult result = RunLibrary("""
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal sealed class BeanStore { }
			internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

			[Module]
			[Singleton<BeanStore>]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule { }
			""");
		string module = ModuleSource(result);
		string handle = Handle("global::Lib.BeanStore");

		await That(result.Diagnostics).IsEmpty()
			.Because("the module registers the internal service the public registration needs");
		await That(module).Contains($"public class Hidden_BeanStore_{Fnv1a("global::Lib.BeanStore"):x8}")
			.Because("the handle is public, so a consumer in another assembly can depend on it");
		await That(module).Contains("internal global::Lib.BeanStore Value { get; }")
			.Because("only the module's assembly can unwrap the handle");
		string instance = Instance("global::Lib.BeanStore");
		await That(module).Contains($"public static {instance} {HiddenFactory("global::Lib.BeanStore")}() => new {instance}(new global::Lib.BeanStore());")
			.Because("the factory wraps the instance in the instance handle of the implementation");
		await That(module).Contains($"public static {handle} {Alias("global::Lib.BeanStore", "global::Lib.BeanStore")}({instance} @awaiten__handle) => new {handle}(@awaiten__handle.Value);")
			.Because("every service of the implementation is served from its instance handle");
		await That(module).Contains($"public static global::Lib.IGrinder {Factory("global::Lib.Grinder")}({handle} @store) => new global::Lib.Grinder(@store.Value);")
			.Because("the factory takes the handle and passes the instance it holds to the constructor");
	}

	[Test]
	public async Task InternalServiceNothingReachableNeeds_GetsNoHandle()
	{
		GeneratorResult result = RunLibrary("""
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal sealed class BeanStore { }
			internal sealed class Grinder : IGrinder { }

			[Module]
			[Singleton<BeanStore>]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule { }
			""");

		await That(ModuleSource(result)).DoesNotContain("Hidden_")
			.Because("no public registration needs the internal service, so a consumer never depends on it");
	}

	[Test]
	public async Task PublicImplementationUnderAnInternalServiceNothingNeeds_CompilesInAContainerInAnotherAssembly()
	{
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
			using Awaiten;

			namespace Lib;

			internal interface IBeanStore { }
			public sealed class BeanStore : IBeanStore { }

			[Module]
			[Singleton<BeanStore, IBeanStore>]
			public static partial class CoffeeModule { }
			""", """
			using Awaiten;

			namespace MyCode;

			[Container]
			[Import(typeof(Lib.CoffeeModule))]
			public static partial class MyContainer { }
			""");

		await That(result.Diagnostics).IsEmpty()
			.Because("the container cannot name the internal service, so it serves the public implementation through the handle the module builds for it");
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).DoesNotContain("global::Lib.IBeanStore")
			.And.DoesNotContain("new global::Lib.BeanStore(");
	}

	[Test]
	public async Task PublicImplementationUnderAnInternalServiceOfAModuleWithoutGeneratedMembers_IsDroppedByAContainerInAnotherAssembly()
	{
		const string library = """
			using Awaiten;

			namespace Lib;

			internal interface IBeanStore { }
			public sealed class BeanStore : IBeanStore { }

			[Module]
			[Singleton<BeanStore, IBeanStore>]
			public static class CoffeeModule { }
			""";

		GeneratorResult libraryResult = Generator.Run(library);
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, """
			using Awaiten;

			namespace MyCode;

			[Container]
			[Import(typeof(Lib.CoffeeModule))]
			public static partial class MyContainer { }
			""");

		await That(libraryResult.Diagnostics).IsEmpty();
		await That(result.Diagnostics).IsEmpty()
			.Because("a module that is not partial builds no handle, and the container cannot name the internal service nor reach it otherwise, so it drops the registration as it does for an internal implementation");
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).DoesNotContain("IBeanStore");
	}
	[Test]
	public async Task EagerInternalSingleton_GetsAHandle()
	{
		GeneratorResult result = RunLibrary("""
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal sealed class Warmer { }
			internal sealed class Grinder : IGrinder { }

			[Module]
			[Singleton<Warmer>(Eager = true)]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule { }
			""");

		await That(ModuleSource(result)).Contains(HiddenFactory("global::Lib.Warmer"))
			.Because("a consumer builds an eager singleton at startup even though nothing depends on it");
	}

	public class ScannedToo
	{
		private static string Library(string registrations, bool grant = false) => $$"""
			using Awaiten;

			{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Friend\")]" : "")}}

			namespace Lib;

			public interface IAppliance { }
			public interface IFront { }
			public interface IBack { }
			public interface IUser { }
			internal interface IHiddenKettle { }
			internal sealed class Kettle : IAppliance, IHiddenKettle, IFront, IBack { }
			internal sealed class User : IUser { public User(IHiddenKettle kettle) { } }

			[Module]
			[Scan<IAppliance>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
			{{registrations}}
			public static partial class CoffeeModule { }
			""";

		[Test]
		[Arguments("[Singleton<Kettle, IHiddenKettle>]\n[Singleton<User, IUser>]")]
		[Arguments("[Singleton<Kettle, IFront>]\n[Singleton<Kettle, IBack>]")]
		public async Task AnImplementationTheModuleBuildsThroughAHandle_ReportsAwt197(string registrations)
		{
			GeneratorResult result = RunLibrary(Library(registrations));

			await That(result.Diagnostics).Contains("*AWT197*'Lib.Kettle'*Lib.IAppliance*").AsWildcard()
				.Because("the scan factory would build a second instance of what the handle shares");
		}

		[Test]
		public async Task AnImplementationUnderAnInternalService_NamesTheInternalService()
		{
			GeneratorResult result = RunLibrary(Library("[Singleton<Kettle, IHiddenKettle>]\n[Singleton<User, IUser>]"));

			await That(result.Diagnostics).Contains("*AWT197*'Lib.Kettle' is registered under the internal service 'Lib.IHiddenKettle'*(Lib.IAppliance, Lib.IHiddenKettle)*").AsWildcard();
		}

		[Test]
		[Arguments("[Singleton<Kettle, IHiddenKettle>]\n[Singleton<User, IUser>]")]
		[Arguments("[Singleton<Kettle, IFront>]\n[Singleton<Kettle, IBack>]")]
		public async Task UnderAGrant_SkipsTheScanFactoryAndTheHandleFactory(string registrations)
		{
			GeneratorResult result = RunLibrary(Library(registrations, grant: true));
			string module = ModuleSource(result);

			await That(result.Diagnostics).IsEmpty()
				.Because("an assembly granted InternalsVisibleTo constructs the implementation once for the scan and the registrations");
			await That(module).DoesNotContain($"{HiddenFactory("global::Lib.Kettle")}(")
				.And.DoesNotContain("public static global::Lib.IAppliance Scan_Kettle_")
				.Because("neither factory may build an instance of its own");
		}
	}

	[Test]
	public async Task InstanceHandleOfAnImplementationTheProviderDoesNotBuild_IsStillDeclaredByIt()
	{
		GeneratorResult result = RunLibrary("""
			using Awaiten;

			[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Friend")]

			namespace Lib;

			public interface IAppliance { }
			public interface IFront { }
			public interface IBack { }
			public interface IUser { }
			internal interface IHiddenKettle { }
			internal sealed class Kettle : IAppliance, IHiddenKettle, IFront, IBack { }
			internal sealed class User : IUser { public User(IHiddenKettle kettle) { } }

			[Module]
			[Scan<IAppliance>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
			[Singleton<Kettle, IFront>]
			[Singleton<Kettle, IBack>]
			public static partial class AModule { }

			[Module]
			[Singleton<Kettle, IHiddenKettle>]
			[Singleton<User, IUser>]
			public static partial class BModule { }
			""");
		string declaring = result.Sources.Single(source => source.Key.Contains("AModule")).Value;
		string user = result.Sources.Single(source => source.Key.Contains("BModule")).Value;

		await That(user).Contains($"global::Lib.AModule.Internals.HiddenInstance_Kettle_{Fnv1a("global::Lib.Kettle"):x8}")
			.Because("the module first by name declares the instance handle every other module builds the implementation into");
		await That(declaring).Contains($"public class HiddenInstance_Kettle_{Fnv1a("global::Lib.Kettle"):x8}")
			.Because("the declaring module emits the handle even when its own scan keeps it from building the implementation");
	}

	public class StartedSingletons
	{
		private static string Library(string registration, bool partial = true, bool grant = false) => $$"""
			using System.Threading.Tasks;
			using Awaiten;

			{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Friend\")]" : "")}}

			namespace Lib;

			public interface IClock { }
			public sealed class Clock : IClock { }
			internal interface IStartable { Task StartAsync(); }
			internal sealed class Warmer : IStartable { public Task StartAsync() => Task.CompletedTask; }
			internal sealed class Boiler { }

			[Module]
			[AsyncInitializer<IStartable>(nameof(IStartable.StartAsync))]
			{{registration}}
			[Singleton<Clock, IClock>]
			public static {{(partial ? "partial " : "")}}class CoffeeModule { }
			""";

		[Test]
		[Arguments("[Singleton<Boiler>(Eager = true)]", "global::Lib.Boiler")]
		[Arguments("[Singleton<Warmer>]", "global::Lib.Warmer")]
		public async Task InternalSingletonAConsumerStarts_GetsAHandleInAModuleWithNothingElseToGenerate(string registration, string implementation)
		{
			GeneratorResult result = RunLibrary(Library(registration));

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains(HiddenFactory(implementation))
				.Because("a consumer builds an eager or async-initialized singleton though nothing depends on it, so the module needs its handle even without any other generated member");
		}

		[Test]
		[Arguments("[Singleton<Boiler>(Eager = true)]", "eager internal singleton 'Lib.Boiler'")]
		[Arguments("[Singleton<Warmer>]", "async-initialized internal singleton 'Lib.Warmer'")]
		public async Task InternalSingletonAConsumerStarts_InAModuleThatIsNotPartial_ReportsAwt194(string registration, string singleton)
		{
			GeneratorResult result = RunLibrary(Library(registration, partial: false));

			await That(result.Diagnostics).Contains($"*AWT194*'Lib.CoffeeModule' registers the {singleton}*not partial*").AsWildcard()
				.Because("a module that cannot receive the handle would let a container in another assembly drop the singleton silently");
		}

		[Test]
		public async Task InternalSingletonAConsumerStarts_InAModuleThatIsNotPartialUnderAGrant_ReportsAwt231InAConsumerWithoutIt()
		{
			string library = Library("[Singleton<Boiler>(Eager = true)]", partial: false, grant: true);

			await That(RunLibrary(library).Diagnostics).IsEmpty()
				.Because("an assembly granted InternalsVisibleTo constructs the singleton itself");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, Consumer.Container).Diagnostics)
				.Contains("*AWT231*'Lib.CoffeeModule' registers 'Lib.Boiler'*no handle for it because the module, or a class that contains it, is not partial*").AsWildcard()
				.Because("a container that can neither name nor construct the singleton reports it instead of silently never building it");
		}

		[Test]
		public async Task InternalSingletonNothingStarts_InAModuleThatIsNotPartial_DoesNotReportAwt194()
		{
			GeneratorResult result = RunLibrary(Library("[Transient<Warmer>]", partial: false));

			await That(result.Diagnostics).IsEmpty()
				.Because("nothing a consumer reaches needs the internal service, so the module needs no handle and no partial modifier");
		}
	}

	[Test]
	public async Task DisposableImplementation_IsWrappedInTheMatchingVariant()
	{
		GeneratorResult result = RunLibrary("""
			using System;
			using System.Threading.Tasks;
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal interface IFilter { }
			internal sealed class PaperFilter : IFilter, IDisposable { public void Dispose() { } }
			internal sealed class MetalFilter : IFilter, IAsyncDisposable { public ValueTask DisposeAsync() => default; }
			internal sealed class ClothFilter : IFilter { }
			internal sealed class Grinder : IGrinder { public Grinder(System.Collections.Generic.IEnumerable<IFilter> filters) { } }

			[Module]
			[Transient<PaperFilter, IFilter>]
			[Transient<MetalFilter, IFilter>]
			[Transient<ClothFilter, IFilter>]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule { }
			""");
		string module = ModuleSource(result);
		string handle = Handle("global::Lib.IFilter");

		await That(result.Diagnostics).IsEmpty();
		await That(module).Contains($"=> new {Instance("global::Lib.PaperFilter")}.Disposable(new global::Lib.PaperFilter());");
		await That(module).Contains($"=> new {Instance("global::Lib.MetalFilter")}.AsyncDisposable(new global::Lib.MetalFilter());");
		await That(module).Contains($"=> new {Instance("global::Lib.ClothFilter")}(new global::Lib.ClothFilter());")
			.Because("each implementation gets the variant that implements exactly its disposal interfaces");
		await That(module).Contains($"public static global::Lib.IGrinder {Factory("global::Lib.Grinder")}(global::System.Collections.Generic.IEnumerable<{handle}> @filters) => new global::Lib.Grinder(global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select(@filters, awaiten__handle => awaiten__handle.Value)));");
	}

	[Test]
	public async Task RelationshipShapes_AreUnwrappedInTheFactory()
	{
		GeneratorResult result = RunLibrary("""
			using System;
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal sealed class BeanStore { }
			internal sealed class Grinder : IGrinder
			{
			    public Grinder(Func<BeanStore> func, Lazy<BeanStore> lazy, Owned<BeanStore> owned, Func<Owned<BeanStore>> factory) { }
			}

			[Module]
			[Transient<BeanStore>]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule { }
			""");
		string module = ModuleSource(result);
		string handle = Handle("global::Lib.BeanStore");

		await That(result.Diagnostics).IsEmpty();
		await That(module).Contains($"(global::System.Func<{handle}> @func, global::System.Lazy<{handle}> @lazy, global::Awaiten.Owned<{handle}> @owned, global::System.Func<global::Awaiten.Owned<{handle}>> @factory)");
		await That(module).Contains("new global::Lib.Grinder(() => @func().Value, new global::System.Lazy<global::Lib.BeanStore>(() => @lazy.Value.Value), global::Awaiten.Internals.AwaitenOwnedProjection.Map(@owned, awaiten__handle => awaiten__handle.Value), () => global::Awaiten.Internals.AwaitenOwnedProjection.Map(@factory(), awaiten__handle => awaiten__handle.Value))");
	}

	[Test]
	public async Task HiddenChain_TakesHandleIntoHandle()
	{
		GeneratorResult result = RunLibrary("""
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal sealed class Silo { }
			internal sealed class BeanStore { public BeanStore(Silo silo) { } }
			internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

			[Module]
			[Singleton<Silo>]
			[Singleton<BeanStore>]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule { }
			""");

		await That(result.Diagnostics).IsEmpty();
		await That(ModuleSource(result)).Contains($"public static {Instance("global::Lib.BeanStore")} {HiddenFactory("global::Lib.BeanStore")}({Handle("global::Lib.Silo")} @silo) => new {Instance("global::Lib.BeanStore")}(new global::Lib.BeanStore(@silo.Value));");
	}

	[Test]
	public async Task ImplementationUnderAnInternalAndAPublicService_SharesOneInstanceThroughAnAlias()
	{
		GeneratorResult result = RunLibrary("""
			using Awaiten;

			namespace Lib;

			public interface IStock { }
			public interface IGrinder { }
			internal interface IBeanStore { }
			internal sealed class BeanStore : IBeanStore, IStock { }
			internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

			[Module]
			[Singleton<BeanStore, IBeanStore>]
			[Singleton<BeanStore, IStock>]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule { }
			""");
		string module = ModuleSource(result);

		await That(result.Diagnostics).IsEmpty()
			.Because("the public service is served from the instance handle, so no second factory splits it");
		await That(module).DoesNotContain($"global::Lib.IStock {Factory("global::Lib.BeanStore")}");
		await That(module).Contains($"public static global::Lib.IStock {Alias("global::Lib.BeanStore", "global::Lib.IStock")}({Instance("global::Lib.BeanStore")} @awaiten__handle) => @awaiten__handle.Value;");
	}

	[Test]
	public async Task SeveralPublicServicesWithoutACoveringType_ShareOneInstanceThroughAHandle()
	{
		GeneratorResult result = RunLibrary("""
			using Awaiten;

			namespace Lib;

			public interface IFront { }
			public interface IBack { }
			internal sealed class Shared : IFront, IBack { }

			[Module]
			[Singleton<Shared, IFront>]
			[Singleton<Shared, IBack>]
			public static partial class CoffeeModule { }
			""");
		string module = ModuleSource(result);
		string handle = Instance("global::Lib.Shared");

		await That(result.Diagnostics).IsEmpty()
			.Because("one handle owns the instance and both services are aliases of it, so no covering type is needed");
		await That(module).Contains($"public static {handle} {HiddenFactory("global::Lib.Shared")}() => new {handle}(new global::Lib.Shared());");
		await That(module).Contains($"public static global::Lib.IFront {Alias("global::Lib.Shared", "global::Lib.IFront")}({handle} @awaiten__handle) => @awaiten__handle.Value;");
		await That(module).Contains($"public static global::Lib.IBack {Alias("global::Lib.Shared", "global::Lib.IBack")}({handle} @awaiten__handle) => @awaiten__handle.Value;");
	}

	[Test]
	public async Task HooksAndConventions_TakeTheHandle()
	{
		GeneratorResult result = RunLibrary("""
			using System.Threading;
			using System.Threading.Tasks;
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal interface IStartable { Task StartAsync(CancellationToken cancellationToken); }
			internal sealed class BeanStore : IStartable { public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
			internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

			[Module]
			[AsyncInitializer<IStartable>(nameof(IStartable.StartAsync))]
			[Singleton<BeanStore>(OnActivated = nameof(Activate), OnRelease = nameof(Release))]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule
			{
			    internal static void Activate(BeanStore store) { }
			    internal static void Release(BeanStore store) { }
			}
			""");
		string module = ModuleSource(result);
		string handle = Instance("global::Lib.BeanStore");

		await That(result.Diagnostics).IsEmpty();
		await That(module).Contains($"({handle} @awaiten__instance) => Activate((global::Lib.BeanStore)@awaiten__instance.Value);")
			.Because("the hook wrapper unwraps the handle for the module's own hook");
		await That(module).Contains($"({handle} @awaiten__instance, global::System.Threading.CancellationToken @cancellationToken) => ((global::Lib.IStartable)@awaiten__instance.Value).StartAsync(@cancellationToken);")
			.Because("the initializer wrapper runs the internal convention on the instance the handle holds");
	}

	[Test]
	public async Task TwoConventionsReachingOneMethod_GetOneInitializerWrapper()
	{
		const string library = """
			using System.Threading;
			using System.Threading.Tasks;
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal interface IStartable { Task StartAsync(CancellationToken cancellationToken); }
			internal interface IWarmable { Task StartAsync(CancellationToken cancellationToken); }
			internal sealed class BeanStore : IStartable, IWarmable { public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
			internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

			[Module]
			[AsyncInitializer<IStartable>(nameof(IStartable.StartAsync))]
			[AsyncInitializer<IWarmable>(nameof(IWarmable.StartAsync))]
			[Singleton<BeanStore>]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule { }
			""";
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, Consumer.Container);
		string module = ModuleSource(RunLibrary(library));
		string container = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

		await That(result.Diagnostics).IsEmpty();
		await That(module.Split("HiddenInitializer_BeanStore_").Length - 1).IsEqualTo(1)
			.Because("both conventions reach the one StartAsync of the internal bean store, which runs once, as in a container of the module's own assembly");
		await That(container.Split("HiddenInitializer_BeanStore_").Length - 1).IsEqualTo(1);
	}

	[Test]
	public async Task FactoryMember_IsWrappedIntoTheHandle()
	{
		GeneratorResult result = RunLibrary("""
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal sealed class Silo { }
			internal sealed class BeanStore { public BeanStore(Silo silo) { } }
			internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

			[Module]
			[Singleton<Silo>]
			[Singleton<BeanStore>(Factory = nameof(CreateStore), OnActivated = nameof(Activate))]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule
			{
			    internal static BeanStore CreateStore(Silo silo) => new(silo);
			    internal static void Activate(BeanStore store) { }
			}
			""");
		string module = ModuleSource(result);
		string handle = Instance("global::Lib.BeanStore");

		await That(result.Diagnostics).IsEmpty()
			.Because("the module wraps its internal factory into the handle, so the public registration can take it");
		await That(module).Contains($"public static {handle} {HiddenFactory("global::Lib.BeanStore")}({Handle("global::Lib.Silo")} @silo) => {handle}.Wrap(CreateStore(@silo.Value));")
			.Because("the factory member's result is wrapped in the handle variant that matches the instance at run time");
		await That(module).Contains($"internal static {handle.Substring(handle.LastIndexOf('.') + 1)} Wrap(global::Lib.BeanStore value)")
			.Because("a factory may return a subtype, so the handle picks its disposal variant when it wraps the instance");
		await That(module).Contains($"({handle} @awaiten__instance) => Activate((global::Lib.BeanStore)@awaiten__instance.Value);");
		await That(module).Contains($"public static global::Lib.IGrinder {Factory("global::Lib.Grinder")}({Handle("global::Lib.BeanStore")} @store) => new global::Lib.Grinder(@store.Value);");
	}

	[Test]
	public async Task AsyncFactoryMember_IsAwaitedIntoTheHandle()
	{
		GeneratorResult result = RunLibrary("""
			using System.Threading.Tasks;
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal sealed class BeanStore { }
			internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

			[Module]
			[Singleton<BeanStore>(Factory = nameof(CreateStoreAsync))]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule
			{
			    internal static Task<BeanStore> CreateStoreAsync() => Task.FromResult(new BeanStore());
			}
			""");
		string handle = Instance("global::Lib.BeanStore");

		await That(result.Diagnostics).IsEmpty();
		await That(ModuleSource(result)).Contains($"public static async global::System.Threading.Tasks.Task<{handle}> {HiddenFactory("global::Lib.BeanStore")}() => {handle}.Wrap(await CreateStoreAsync().ConfigureAwait(false));")
			.Because("an async factory member gets an async wrapper, which a consumer awaits like any async factory");
	}

	[Test]
	public async Task InstanceMember_IsWrappedIntoTheHandle()
	{
		GeneratorResult result = RunLibrary("""
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal sealed class BeanStore { }
			internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

			[Module]
			[Singleton<BeanStore>(Instance = nameof(Store))]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule
			{
			    internal static readonly BeanStore Store = new();
			}
			""");
		string handle = Instance("global::Lib.BeanStore");

		await That(result.Diagnostics).IsEmpty();
		await That(ModuleSource(result)).Contains($"public static {handle} {HiddenFactory("global::Lib.BeanStore")} => new {handle}(Store);")
			.Because("a pre-built instance needs no disposal variant, since no container owns it");
	}

	[Test]
	public async Task DecoratorOfAnInternalService_IsBuiltIntoTheHandle()
	{
		GeneratorResult result = RunLibrary("""
			using System;
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal interface IBeanStore { }
			internal sealed class BeanStore : IBeanStore { }
			internal sealed class LoggingStore : IBeanStore, IDisposable { public LoggingStore(IBeanStore inner) { } public void Dispose() { } }
			internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

			[Module]
			[Singleton<BeanStore, IBeanStore>]
			[Singleton<Grinder, IGrinder>]
			[Decorate<LoggingStore, IBeanStore>]
			public static partial class CoffeeModule { }
			""");
		string handle = Handle("global::Lib.IBeanStore");
		string decorator = $"Decorator_LoggingStore_{Fnv1a("global::Lib.LoggingStore\0global::Lib.IBeanStore"):x8}";

		await That(result.Diagnostics).IsEmpty();
		await That(ModuleSource(result)).Contains($"public static {handle} {decorator}({handle} @inner) => new {handle}.Disposable(new global::Lib.LoggingStore(@inner.Value));")
			.Because("the decorator takes the inner link as a handle and is wrapped in a handle of its own, in its own disposal variant");
	}

	[Test]
	public async Task DecoratorOfAnInternalServiceWhoseImplementationHasAPublicServiceToo_IsBuiltIntoTheHandle()
	{
		GeneratorResult result = RunLibrary("""
			using Awaiten;

			namespace Lib;

			public interface IStock { }
			public interface IGrinder { }
			internal interface IBeanStore { }
			internal sealed class BeanStore : IBeanStore, IStock { }
			internal sealed class LoggingStore : IBeanStore { public LoggingStore(IBeanStore inner) { } }
			internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

			[Module]
			[Singleton<BeanStore, IBeanStore>]
			[Singleton<BeanStore, IStock>]
			[Singleton<Grinder, IGrinder>]
			[Decorate<LoggingStore, IBeanStore>]
			public static partial class CoffeeModule { }
			""");

		await That(result.Diagnostics).IsEmpty();
		await That(ModuleSource(result)).Contains("Decorator_LoggingStore_")
			.Because("every service is an alias of the instance handle, so decorating the internal service leaves the public one undecorated, as in a container of the module's own assembly");
	}

	[Test]
	public async Task OptionalAndDeferredProperties_AreMirroredOntoTheFactory()
	{
		GeneratorResult result = RunLibrary("""
			using Awaiten;

			namespace Lib;

			public interface IClock { }
			public interface IAudit { }
			public interface IGrinder { }
			internal sealed class Grinder : IGrinder
			{
			    [Inject(Optional = true)] public IClock Clock { get; set; }
			    [Inject(Deferred = true)] public IAudit Audit { get; set; } = null!;
			}

			[Module]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule { }
			""");
		string module = ModuleSource(result);
		string factory = Factory("global::Lib.Grinder");

		await That(result.Diagnostics).IsEmpty()
			.Because("an optional property becomes an optional factory parameter and a deferred one gets a setter wrapper");
		await That(module).Contains($"public static global::Lib.IGrinder {factory}(global::Lib.IClock @Clock = default!, bool awaiten__has_Clock = false)")
			.And.Contains("if (awaiten__has_Clock)")
			.And.Contains("awaiten__instance.Clock = @Clock;")
			.Because("a consumer marks a registered optional service present, so the factory assigns it even when it resolved to null and leaves the property as it is for a missing one");
		await That(module).Contains($"public static void Deferred_{factory}_Audit(global::Lib.IGrinder @awaiten__instance, global::Lib.IAudit @Audit) => ((global::Lib.Grinder)@awaiten__instance).Audit = @Audit;")
			.Because("a consumer assigns a deferred property after it built and cached the instance, so it can break a cycle");
	}

	[Test]
	public async Task InternalServiceOfAnotherModule_IsTakenThroughThatModulesHandle()
	{
		GeneratorResult result = RunLibrary("""
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal sealed class BeanStore { }
			internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

			[Module]
			[Singleton<BeanStore>]
			public static partial class StorageModule { }

			[Module]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule { }
			""");
		string handle = $"global::Lib.StorageModule.Internals.Hidden_BeanStore_{Fnv1a("global::Lib.BeanStore"):x8}";

		await That(result.Diagnostics).IsEmpty()
			.Because("a module may take the handle another module of its assembly declares");
		await That(result.Sources.Single(source => source.Key.Contains("CoffeeModule")).Value)
			.Contains($"public static global::Lib.IGrinder {Factory("global::Lib.Grinder")}({handle} @store) => new global::Lib.Grinder(@store.Value);");
		await That(result.Sources.Single(source => source.Key.Contains("StorageModule")).Value)
			.Contains($"public static global::Lib.StorageModule.Internals.HiddenInstance_BeanStore_{Fnv1a("global::Lib.BeanStore"):x8} {HiddenFactory("global::Lib.BeanStore")}()")
			.Because("in an assembly with several modules, each module builds every internal service it can, since another module may need it");
	}

	[Test]
	public async Task SameAssemblyContainer_ConstructsTheInternalServiceDirectly()
	{
		GeneratorResult result = Generator.Run("""
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal sealed class BeanStore { }
			internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

			[Module]
			[Singleton<BeanStore>]
			[Singleton<Grinder, IGrinder>]
			public static partial class CoffeeModule { }

			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class CoffeeContainer { }
			""");
		string container = result.Sources.Single(source => source.Key.Contains("CoffeeContainer")).Value;

		await That(result.Diagnostics).IsEmpty();
		await That(container).DoesNotContain("Hidden_")
			.Because("a container in the module's own assembly names the internal service and never uses a handle");
	}

	public class Consumer
	{
		internal const string Container = """
		                                 using Awaiten;
		                                 using Lib;

		                                 namespace MyCode;

		                                 [Container]
		                                 [Import(typeof(CoffeeModule))]
		                                 public static partial class MyContainer;
		                                 """;

		[Test]
		public async Task RegistersTheHandleAsADependencyOnly()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class BeanStore { }
				internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

				[Module]
				[Singleton<BeanStore>]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }
				""", Container);
			string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
			string handle = Handle("global::Lib.BeanStore");

			await That(result.Diagnostics).IsEmpty();
			await That(source).Contains($"global::Lib.CoffeeModule.Internals.{HiddenFactory("global::Lib.BeanStore")}()")
				.Because("the container builds the internal service through the module's handle factory");
			await That(source).Contains("global::Awaiten.IAwaitenResolver<global::Lib.IGrinder>");
			await That(source).DoesNotContain($"global::Awaiten.IAwaitenResolver<{handle}>")
				.And.DoesNotContain($"typeof({handle})")
				.And.DoesNotContain($"<{handle}>>")
				.Because("a handle is never offered for resolution, advertised as a registration or dispatched by type");
		}

		[Test]
		public async Task CaptiveDependency_NamesTheInternalService()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class BeanStore { }
				internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

				[Module]
				[Scoped<BeanStore>]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }
				""", Container);

			await That(result.Diagnostics).Contains("*AWT105*'Lib.BeanStore (internal to module Lib.CoffeeModule)'*").AsWildcard()
				.Because("the lifetime checks work through the handle and name the internal service instead of the generated type");
			await That(result.Diagnostics).DoesNotContain("*Hidden_*").AsWildcard();
		}

		[Test]
		public async Task MissingDependency_NamesTheInternalService()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IClock { }
				public interface IGrinder { }
				internal sealed class BeanStore { public BeanStore(IClock clock) { } }
				internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

				[Module]
				[Singleton<BeanStore>]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }
				""", Container);

			await That(result.Diagnostics).Contains("*AWT101*'Lib.BeanStore (internal to module Lib.CoffeeModule)'*Lib.IClock*").AsWildcard()
				.Because("the container resolves the internal service's dependencies from its own graph and names the service when one is missing");
			await That(result.Diagnostics).DoesNotContain("*Hidden_*").AsWildcard();
		}

		[Test]
		public async Task InternalServiceOfAnotherModule_IsResolvedFromThatModule()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(StorageAndCoffee, """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(StorageModule))]
				[Import(typeof(CoffeeModule))]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).IsEmpty()
				.Because("the storage module registers the handle the coffee module's factory takes");
		}

		private const string StorageAndCoffee = """
		                                        using Awaiten;

		                                        namespace Lib;

		                                        public interface IGrinder { }
		                                        internal sealed class BeanStore { }
		                                        internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

		                                        [Module]
		                                        [Singleton<BeanStore>]
		                                        public static partial class StorageModule { }

		                                        [Module]
		                                        [Singleton<Grinder, IGrinder>]
		                                        public static partial class CoffeeModule { }
		                                        """;

		[Test]
		public async Task TwoModulesRegisteringOneInternalImplementation_ShareOneInstance()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				public interface IRoaster { }
				internal sealed class BeanStore { }
				internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }
				internal sealed class Roaster : IRoaster { public Roaster(BeanStore store) { } }

				[Module]
				[Singleton<BeanStore>]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }

				[Module]
				[Singleton<BeanStore>]
				[Singleton<Roaster, IRoaster>]
				public static partial class RoastingModule { }
				""", """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(CoffeeModule))]
				[Import(typeof(RoastingModule))]
				public static partial class MyContainer;
				""");

			string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

			await That(result.Diagnostics).IsEmpty()
				.Because("both registrations coalesce into one, as in a container of the modules' own assembly");
			await That(source).Contains($"global::Lib.CoffeeModule.Internals.{HiddenFactory("global::Lib.BeanStore")}()")
				.And.DoesNotContain($"global::Lib.RoastingModule.Internals.{HiddenFactory("global::Lib.BeanStore")}()")
				.Because("the module first by name declares the handle and builds the one shared instance");
		}

		[Test]
		public async Task FactoryAndInstanceMembers_AreRegisteredThroughTheirHandleWrappers()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using System.Threading.Tasks;
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				public interface IStock { }
				internal interface IClock { }
				internal sealed class Clock : IClock { }
				internal sealed class BeanStore : IStock { }
				internal sealed class Grinder : IGrinder { public Grinder(BeanStore store, IClock clock) { } }

				[Module]
				[Singleton<BeanStore>(Factory = nameof(CreateStoreAsync))]
				[Singleton<BeanStore, IStock>(Factory = nameof(CreateStoreAsync))]
				[Singleton<IClock>(Instance = nameof(Clock))]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule
				{
				    internal static readonly IClock Clock = new Clock();
				    internal static Task<BeanStore> CreateStoreAsync() => Task.FromResult(new BeanStore());
				}
				""", Container);
			string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

			await That(result.Diagnostics).IsEmpty();
			await That(source).Contains($"global::Lib.CoffeeModule.Internals.{HiddenFactory("global::Lib.BeanStore")}()")
				.And.Contains($"global::Lib.CoffeeModule.Internals.{HiddenFactory("global::Lib.IClock")}")
				.And.Contains($"global::Lib.CoffeeModule.Internals.{Alias("global::Lib.BeanStore", "global::Lib.IStock")}(")
				.Because("the container builds each internal service through the module's wrapper and serves the public service from the handle");
		}

		[Test]
		public async Task DecoratorsOfAnInternalService_DecorateItsHandleInOrder()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal interface IBeanStore { }
				internal sealed class BeanStore : IBeanStore { }
				internal sealed class LoggingStore : IBeanStore { public LoggingStore(IBeanStore inner) { } }
				internal sealed class CachingStore : IBeanStore { public CachingStore(IBeanStore inner) { } }
				internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

				[Module]
				[Singleton<BeanStore, IBeanStore>]
				[Singleton<Grinder, IGrinder>]
				[Decorate<LoggingStore, IBeanStore>]
				[Decorate<CachingStore, IBeanStore>]
				public static partial class CoffeeModule { }
				""", Container);
			string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
			string handle = Handle("global::Lib.IBeanStore");
			string logging = $"Decorator_LoggingStore_{Fnv1a("global::Lib.LoggingStore\0global::Lib.IBeanStore"):x8}";
			string caching = $"Decorator_CachingStore_{Fnv1a("global::Lib.CachingStore\0global::Lib.IBeanStore"):x8}";

			await That(result.Diagnostics).IsEmpty();
			await That(source).Contains($"global::Lib.CoffeeModule.Internals.{logging}(")
				.And.Contains($"global::Lib.CoffeeModule.Internals.{caching}(")
				.Because("the container decorates the handle through the module's decorator factories");
			await That(source).DoesNotContain($"global::Awaiten.IAwaitenResolver<{handle}>")
				.Because("a decorated handle stays a dependency only");
		}

		[Test]
		public async Task DecoratorOfAnInternalServiceAnotherModuleRegisters_DecoratesItsHandle()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal interface IBeanStore { }
				internal sealed class BeanStore : IBeanStore { }
				internal sealed class LoggingStore : IBeanStore, System.IDisposable { public LoggingStore(IBeanStore inner) { } public void Dispose() { } }
				internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

				[Module]
				[Singleton<BeanStore, IBeanStore>]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }

				[Module]
				[Decorate<LoggingStore, IBeanStore>]
				public static partial class LoggingModule { }
				""", """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(CoffeeModule))]
				[Import(typeof(LoggingModule))]
				public static partial class MyContainer;
				""");
			string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
			string logging = $"Decorator_LoggingStore_{Fnv1a("global::Lib.LoggingStore\0global::Lib.IBeanStore"):x8}";

			await That(result.Diagnostics).IsEmpty()
				.Because("a container in the modules' own assembly decorates the service another module registers");
			await That(source).Contains($"global::Lib.LoggingModule.Internals.{logging}(")
				.Because("the decorating module builds its decorator into the handle the registering module declares");
		}

		[Test]
		public async Task CompositeOfAnInternalService_IsBuiltIntoItsHandle()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using System.Collections.Generic;
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal interface IBeanStore { }
				internal sealed class BeanStore : IBeanStore { }
				internal sealed class AllStores : IBeanStore { public AllStores(IEnumerable<IBeanStore> stores) { } }
				internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

				[Module]
				[Singleton<BeanStore, IBeanStore>]
				[Singleton<Grinder, IGrinder>]
				[Composite<AllStores, IBeanStore>]
				public static partial class CoffeeModule { }
				""", Container);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains($"global::Lib.CoffeeModule.Internals.Composite_AllStores_{Fnv1a("global::Lib.AllStores\0global::Lib.IBeanStore"):x8}(")
				.Because("the module builds the composite into a handle of the internal service, which a container in another assembly fronts it with");
		}

		[Test]
		public async Task OptionalAndDeferredProperties_AreResolvedThroughTheFactory()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IClock { }
				public interface IAudit { }
				public interface IGrinder { }
				internal sealed class Grinder : IGrinder
				{
				    [Inject(Optional = true)] public IClock Clock { get; set; }
				    [Inject(Deferred = true)] public IAudit Audit { get; set; } = null!;
				}

				[Module]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }
				""", """
				using Awaiten;
				using Lib;

				namespace MyCode;

				public sealed class Audit : IAudit { public Audit(IGrinder grinder) { } }

				[Container]
				[Import(typeof(CoffeeModule))]
				[Transient<Audit, IAudit>]
				public static partial class MyContainer;
				""");
			string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
			string factory = Factory("global::Lib.Grinder");

			await That(result.Diagnostics).IsEmpty()
				.Because("the missing optional clock is no missing dependency, and the deferred audit breaks the cycle with the grinder");
			await That(source).Contains($"global::Lib.CoffeeModule.Internals.{factory}(default!, default!)")
				.Because("the container passes neither a value nor the presence flag for an optional service it does not register");
			await That(source).Contains($"global::Lib.CoffeeModule.Internals.Deferred_{factory}_Audit(")
				.Because("the container assigns the deferred property through the module's setter wrapper");
		}

		[Test]
		public async Task TransientDisposableThroughAnAlias_IsWithheldOnTheRootUnderStrictLifetimeSafety()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IPublic { }
				internal interface IHidden { }
				internal sealed class Impl : IPublic, IHidden, System.IDisposable { public void Dispose() { } }

				[Module]
				[Transient<Impl, IPublic>]
				[Transient<Impl, IHidden>]
				public static partial class CoffeeModule { }
				""";
			GeneratorResult same = Generator.Run(library + """

				[Container]
				[Import(typeof(CoffeeModule))]
				public static partial class SameContainer;
				""");
			GeneratorResult cross = Generator.RunWithGeneratedReferencedAssembly(library, Container);
			string sameSource = same.Sources["Awaiten.Lib.SameContainer.g.cs"];
			string crossSource = cross.Sources["Awaiten.MyCode.MyContainer.g.cs"];

			await That(sameSource).DoesNotContain("global::Awaiten.IAwaitenResolver<global::Lib.IPublic>")
				.And.Contains("'Lib.IPublic' is withheld from by-type resolution on the container root");
			await That(crossSource).DoesNotContain("global::Awaiten.IAwaitenResolver<global::Lib.IPublic>")
				.And.Contains("'Lib.IPublic' is withheld from by-type resolution on the container root")
				.Because("the alias builds a transient disposable through the handle, which accumulates on the root as the implementation does in the module's own assembly");
		}

		public class PublicImplementation
		{
			private const string Library = """
				using Awaiten;

				namespace Lib;

				internal interface IStore { }
				public sealed class Store : IStore { }
				public interface IGrinder { }
				internal sealed class Grinder : IGrinder { public Grinder(IStore store) { } }

				[Module]
				[Singleton<Store, IStore>]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }
				""";

			private static string Consumer(string registration) => $$"""
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(CoffeeModule))]
				{{registration}}
				public static partial class MyContainer;
				""";

			[Test]
			public async Task RegisteredByTheConsumerToo_SharesTheInstanceOfTheHandle()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library, Consumer("[Singleton<Store>]"));
				string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

				await That(result.Diagnostics).IsEmpty();
				await That(source).DoesNotContain("new global::Lib.Store(")
					.Because("the container's registration serves the one instance the handle holds, as a container in the module's own assembly coalesces both");
				await That(source).Contains($"global::Lib.CoffeeModule.Internals.{Alias("global::Lib.Store", "global::Lib.Store")}(");
			}

			[Test]
			public async Task RegisteredByAModuleOfTheConsumer_SharesTheInstanceOfTheHandle()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library, Consumer("[Import(typeof(StoreModule))]") + """

					[Module]
					[Singleton<Store>]
					public static class StoreModule { }
					""");
				string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

				await That(result.Diagnostics).IsEmpty();
				await That(source).DoesNotContain("new global::Lib.Store(")
					.Because("a module of another assembly that registers the public implementation shares the handle's instance too");
			}

			[Test]
			public async Task WithALifetimeOfItsOwn_ReportsAwt107()
			{
				GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library, Consumer("[Transient<Store>]"));

				await That(result.Diagnostics).Contains("*AWT107*'Lib.Store'*").AsWildcard()
					.Because("a container in the module's own assembly reports the conflicting lifetime of the one implementation");
			}
		}

		public class FactoryOfTheServiceReplaced
		{
			private static string Library(bool suppressDisposal) => $$"""
				using Awaiten;

				namespace Lib;

				public interface IClock { }
				internal sealed class TimeSource { }
				internal sealed class SystemClock : IClock { public SystemClock(TimeSource source) { } }

				[Module]
				[Singleton<TimeSource>]
				[Singleton<IClock>(Factory = nameof(CreateClock){{(suppressDisposal ? ", SuppressDisposal = true" : "")}})]
				public static partial class ClockModule
				{
				    internal static IClock CreateClock(TimeSource source) => new SystemClock(source);
				}
				""";

			private const string Extending = """
				using Awaiten;
				using Lib;

				namespace MyCode;

				public sealed class FakeClock : IClock { }

				[Container]
				[Import(typeof(ClockModule))]
				public static partial class Shop;

				[Container(Extends = typeof(Shop))]
				[Singleton<IClock>(Factory = nameof(CreateFakeClock))]
				public static partial class TestShop
				{
				    private static IClock CreateFakeClock() => new FakeClock();
				}
				""";

			private const string Importing = """
				using Awaiten;
				using Lib;

				namespace MyCode;

				public sealed class FakeClock : IClock { }

				[Container]
				[Import(typeof(ClockModule))]
				[Singleton<IClock>(Factory = nameof(CreateFakeClock))]
				public static partial class TestShop
				{
				    private static IClock CreateFakeClock() => new FakeClock();
				}
				""";

			[Test]
			[Arguments(false, false)]
			[Arguments(false, true)]
			[Arguments(true, false)]
			[Arguments(true, true)]
			public async Task ByAnExtendingContainer_ServesTheReplacementWithoutBuildingTheHandle(bool suppressDisposal, bool referenceAssembly)
			{
				GeneratorResult result = referenceAssembly
					? Generator.RunWithGeneratedReferenceAssembly(Library(suppressDisposal), Extending)
					: Generator.RunWithGeneratedReferencedAssembly(Library(suppressDisposal), Extending);
				string source = result.Sources["Awaiten.MyCode.TestShop.g.cs"];

				await That(result.Diagnostics).IsEmpty()
					.Because("the factory only names its service, so the extending container's factory replaces it like any other inherited registration");
				await That(result.Sources["Awaiten.MyCode.Shop.g.cs"]).Contains($"global::Lib.ClockModule.Internals.{HiddenFactory("global::Lib.IClock")}(");
				await That(source).Contains("__s._iClock = CreateFakeClock();").And.DoesNotContain(HiddenFactory("global::Lib.IClock")).And.DoesNotContain(Alias("global::Lib.IClock", "global::Lib.IClock"))
					.Because("the replaced factory is served neither through its handle nor through the alias of the handle");
			}

			[Test]
			[Arguments(false, false)]
			[Arguments(false, true)]
			[Arguments(true, false)]
			[Arguments(true, true)]
			public async Task ByAnImportingContainer_ServesTheReplacementWithoutBuildingTheHandle(bool suppressDisposal, bool referenceAssembly)
			{
				GeneratorResult result = referenceAssembly
					? Generator.RunWithGeneratedReferenceAssembly(Library(suppressDisposal), Importing)
					: Generator.RunWithGeneratedReferencedAssembly(Library(suppressDisposal), Importing);
				string source = result.Sources["Awaiten.MyCode.TestShop.g.cs"];

				await That(result.Diagnostics).IsEmpty()
					.Because("both factories only name their service, so they are two implementations of it, as in a container of the module's own assembly");
				await That(source).Contains("__s._iClock = CreateFakeClock();").And.Contains($"global::Lib.ClockModule.Internals.{HiddenFactory("global::Lib.IClock")}(")
					.Because("the container's own factory serves the single resolution, while the module's factory stays a collection member");
			}

			[Test]
			public async Task ImplementationTheModuleNames_StillConflictsWithAnotherFactoryOfIt()
			{
				string library = Library(suppressDisposal: false)
					.Replace("internal sealed class SystemClock : IClock { public SystemClock", "public sealed class SystemClock : IClock { internal SystemClock")
					.Replace("[Singleton<IClock>(Factory = nameof(CreateClock))]", "[Singleton<SystemClock, IClock>(Factory = nameof(CreateClock))]")
					.Replace("internal static IClock CreateClock", "internal static SystemClock CreateClock");
				const string container = """
					[Container]
					[Import(typeof(ClockModule))]
					[Singleton<SystemClock>(Factory = nameof(CreateOtherClock))]
					public static partial class {0}
					{
					    private static SystemClock CreateOtherClock() => null!;
					}
					""";
				GeneratorResult same = Generator.Run(library + "\n" + container.Replace("{0}", "SameContainer"));
				GeneratorResult cross = Generator.RunWithGeneratedReferencedAssembly(library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + container.Replace("{0}", "MyContainer"));

				await That(same.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT111"));
				await That(cross.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT111"))
					.Because("the module names the implementation its factory produces, so the container's factory of it is a second production of the one instance");
			}

			private const string ClassLibrary = """
				using Awaiten;

				namespace Lib;

				public sealed class Clock { }
				internal sealed class TimeSource { }

				[Module]
				[Singleton<TimeSource>]
				[Singleton<Clock>(Factory = nameof(CreateClock), SuppressDisposal = true{0})]
				public static partial class ClockModule
				{
				    internal static Clock CreateClock(TimeSource source) => new Clock();
				}
				""";

			[Test]
			[Arguments(false)]
			[Arguments(true)]
			public async Task ByARegistrationOfTheImplementationOnAnExtendingContainer_ServesItWithoutTheHandle(bool referenceAssembly)
			{
				string library = ClassLibrary.Replace("{0}", "");
				const string extending = """
					using Awaiten;
					using Lib;

					namespace MyCode;

					[Container]
					[Import(typeof(ClockModule))]
					public static partial class Shop;

					[Container(Extends = typeof(Shop))]
					[Transient<Clock>]
					public static partial class TestShop;
					""";
				GeneratorResult result = referenceAssembly
					? Generator.RunWithGeneratedReferenceAssembly(library, extending)
					: Generator.RunWithGeneratedReferencedAssembly(library, extending);

				await That(result.Diagnostics).IsEmpty()
					.Because("the extending container's registration replaces the module's factory in full, so another lifetime, production or SuppressDisposal is no conflict");
				await That(result.Sources["Awaiten.MyCode.TestShop.g.cs"]).Contains("new global::Lib.Clock()").And.DoesNotContain(HiddenFactory("global::Lib.Clock"))
					.Because("the replaced factory never runs, so the container constructs the implementation itself");
			}

			[Test]
			[Arguments(false)]
			[Arguments(true)]
			public async Task ByARegistrationOfTheImplementationOverAnOverridableDefault_ServesItWithoutTheHandle(bool referenceAssembly)
			{
				string library = ClassLibrary.Replace("{0}", ", Fallback = Fallback.Silent");
				const string importing = """
					using Awaiten;
					using Lib;

					namespace MyCode;

					[Container]
					[Import(typeof(ClockModule))]
					[Transient<Clock>]
					public static partial class TestShop;
					""";
				GeneratorResult result = referenceAssembly
					? Generator.RunWithGeneratedReferenceAssembly(library, importing)
					: Generator.RunWithGeneratedReferencedAssembly(library, importing);

				await That(result.Diagnostics).IsEmpty()
					.Because("the container's registration drops the module's overridable default in full, as in the module's own assembly");
				await That(result.Sources["Awaiten.MyCode.TestShop.g.cs"]).Contains("new global::Lib.Clock()").And.DoesNotContain(HiddenFactory("global::Lib.Clock"));
			}

			[Test]
			public async Task ByARegistrationOfTheImplementationBesideTheModulesFactory_StillConflicts()
			{
				string library = ClassLibrary.Replace("{0}", "");
				const string container = """
					[Container]
					[Import(typeof(ClockModule))]
					[Singleton<Clock>]
					public static partial class {0};
					""";
				GeneratorResult same = Generator.Run(library + "\n" + container.Replace("{0}", "SameContainer"));
				GeneratorResult cross = Generator.RunWithGeneratedReferencedAssembly(library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + container.Replace("{0}", "MyContainer"));

				await That(same.Diagnostics).Contains(diagnostic => diagnostic.Contains("AWT111"));
				await That(cross.Diagnostics).Contains(diagnostic => diagnostic.Contains("AWT111"))
					.Because("the module's factory still produces the implementation the container constructs, so both are one instance with two productions");
			}
		}

		public class SeveralProducersOfOneService
		{
			private const string OneModule = """
				using Awaiten;

				namespace Lib;

				public interface IClock { }
				internal sealed class TimeSource { }
				internal sealed class SystemClock : IClock { public SystemClock(TimeSource source) { } }

				[Module]
				[Singleton<TimeSource>]
				[Singleton<IClock>(Factory = nameof(CreateA))]
				[Singleton<IClock>(Factory = nameof(CreateB))]
				public static partial class ClockModule
				{
				    internal static IClock CreateA(TimeSource source) => new SystemClock(source);
				    internal static IClock CreateB(TimeSource source) => new SystemClock(source);
				}
				""";

			private const string TwoModules = """
				using Awaiten;

				namespace Lib;

				public interface IClock { }
				internal sealed class TimeSource { }
				internal sealed class SystemClock : IClock { public SystemClock(TimeSource source) { } }

				[Module]
				[Singleton<TimeSource>]
				[Singleton<IClock>(Factory = nameof(CreateClock))]
				public static partial class ClockModule
				{
				    internal static IClock CreateClock(TimeSource source) => new SystemClock(source);
				}

				[Module]
				[Singleton<IClock>(Factory = nameof(CreateB))]
				public static partial class OtherModule
				{
				    internal static IClock CreateB(TimeSource source) => new SystemClock(source);
				}
				""";

			private const string CollectionContainer = """
				public sealed class Shop(System.Collections.Generic.IEnumerable<IClock> clocks)
				{
				    public System.Collections.Generic.IEnumerable<IClock> Clocks => clocks;
				}

				[Container]
				[Import(typeof(ClockModule))]{1}
				[Singleton<Shop>]
				public static partial class {0};
				""";

			private static GeneratorResult Run(string library, string container, string consumer)
			{
				const string usings = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n";
				return consumer switch
				{
					"same" => Generator.Run(library + "\n" + container.Replace("{0}", "SameContainer")),
					"friend" => Generator.RunWithGeneratedReferencedAssembly(
						library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\n\nnamespace Lib;"),
						usings + container.Replace("{0}", "MyContainer")),
					"reference" => Generator.RunWithGeneratedReferenceAssembly(library, usings + container.Replace("{0}", "MyContainer")),
					_ => Generator.RunWithGeneratedReferencedAssembly(library, usings + container.Replace("{0}", "MyContainer")),
				};
			}

			private static string ProducerFactory(string module, string implementation, string producer)
				=> $"global::Lib.{module}.Internals.HiddenRegistration_{SimpleName(implementation)}_{Fnv1a($"{implementation}\0{producer}"):x8}(";

			[Test]
			public async Task OneModule_BuildsTheLibraryWithoutAConflict()
			{
				GeneratorResult result = Generator.Run(OneModule);

				await That(result.Diagnostics).IsEmpty()
					.Because("two factories that name only their service are two producers of it, not two productions of one implementation");
				await That(ModuleSource(result)).Contains("HiddenRegistration_IClock_").And.Contains("CreateA(").And.Contains("CreateB(");
			}

			[Test]
			[Arguments("same")]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task OneModule_ServesBothFactoriesInTheCollection(string consumer)
			{
				GeneratorResult result = Run(OneModule, CollectionContainer.Replace("{1}", ""), consumer);

				await That(result.Diagnostics).IsEmpty()
					.Because("a container of the module's own assembly keeps both factories as two members of the collection");
				if (consumer is "full" or "reference")
				{
					await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"])
						.Contains(ProducerFactory("ClockModule", "global::Lib.IClock", "CreateA"))
						.And.Contains(ProducerFactory("ClockModule", "global::Lib.IClock", "CreateB"))
						.Because("each factory has a handle of its own, so the container calls both");
				}
			}

			[Test]
			[Arguments("same")]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task TwoModules_ServesBothFactoriesInTheCollection(string consumer)
			{
				GeneratorResult result = Run(TwoModules, CollectionContainer.Replace("{1}", "\n[Import(typeof(OtherModule))]"), consumer);

				await That(result.Diagnostics).IsEmpty()
					.Because("factories of two modules that name only their service are two producers of it, as in the modules' own assembly");
				if (consumer is "full" or "reference")
				{
					await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"])
						.Contains(ProducerFactory("ClockModule", "global::Lib.IClock", "CreateClock"))
						.And.Contains(ProducerFactory("OtherModule", "global::Lib.IClock", "CreateB"))
						.Because("each module builds its factory into an instance handle of its own");
				}
			}

			[Test]
			[Arguments("same")]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task ConstructedThroughTheModulesFactory_ConflictsWithAFactoryOfTheContainer(string consumer)
			{
				const string library = """
					using Awaiten;

					namespace Lib;

					internal sealed class TimeSource { }
					public sealed class Clock { internal Clock(TimeSource source) { } }

					[Module]
					[Singleton<TimeSource>]
					[Singleton<Clock>]
					public static partial class ClockModule;
					""";
				const string container = """
					[Container]
					[Import(typeof(ClockModule))]
					[Singleton<Clock>(Factory = nameof(CreateFake))]
					public static partial class {0}
					{
					    private static Clock CreateFake() => null!;
					}
					""";
				GeneratorResult result = Run(library, container, consumer);

				await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT111") && diagnostic.Contains("a constructor"))
					.Because("the module constructs the implementation, even through its generated factory, so the container's factory of it is a second production of the one instance");
			}

			[Test]
			[Arguments("same")]
			[Arguments("friend")]
			[Arguments("full")]
			[Arguments("reference")]
			public async Task ClassProducedByTwoFactories_ConflictsWithAConstructorOfTheContainer(string consumer)
			{
				const string library = """
					using Awaiten;

					namespace Lib;

					internal sealed class TimeSource { }
					public sealed class Clock { public Clock() { } }

					[Module]
					[Singleton<TimeSource>]
					[Singleton<Clock>(Factory = nameof(CreateA))]
					[Singleton<Clock>(Factory = nameof(CreateB))]
					public static partial class ClockModule
					{
					    internal static Clock CreateA(TimeSource source) => new();
					    internal static Clock CreateB(TimeSource source) => new();
					}
					""";
				const string container = """
					[Container]
					[Import(typeof(ClockModule))]
					[Singleton<Clock>]
					public static partial class {0};
					""";
				GeneratorResult result = Run(library, container, consumer);

				await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT111") && diagnostic.Contains("a constructor and factory 'Lib.ClockModule.CreateA'"))
					.Because("the container's constructor names the class as its implementation, so the module's factories of it are further productions of the one instance, as in the module's own assembly");
			}

			public class OfTheImportedModules
			{
				private const string ThreeModules = """
					using Awaiten;

					namespace Lib;

					internal sealed class TimeSource { }
					public sealed class Clock : System.IDisposable { internal Clock(TimeSource source) { } public void Dispose() { } }

					[Module]
					[Singleton<TimeSource>]
					[Singleton<Clock>(Factory = nameof(CreateA))]
					public static partial class AModule
					{
					    internal static Clock CreateA(TimeSource source) => new(source);
					}

					[Module]
					[{0}<Clock>(Factory = nameof(CreateB))]
					public static partial class BModule
					{
					    internal static Clock CreateB(TimeSource source) => new(source);
					}

					[Module]
					[Singleton<Clock>]
					public static partial class CModule;
					""";

				[Test]
				[Arguments("same", "Singleton")]
				[Arguments("friend", "Singleton")]
				[Arguments("full", "Singleton")]
				[Arguments("reference", "Singleton")]
				[Arguments("same", "Scoped")]
				[Arguments("friend", "Scoped")]
				[Arguments("full", "Scoped")]
				[Arguments("reference", "Scoped")]
				public async Task WhenNoneNamesTheClass_AreTwoProducers(string consumer, string lifetime)
				{
					const string container = """
						[Container]
						[Import(typeof(AModule))]
						[Import(typeof(BModule))]
						public static partial class {0};
						""";
					GeneratorResult result = Run(ThreeModules.Replace("{0}", lifetime), container, consumer);

					await That(result.Diagnostics).IsEmpty()
						.Because("neither imported module names the class as its implementation, so a container of the modules' own assembly keeps both factories as producers of their own, whatever another module of the assembly registers");
					if (consumer is "full" or "reference")
					{
						await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"])
							.Contains(ProducerFactory("AModule", "global::Lib.Clock", "CreateA"))
							.And.Contains(ProducerFactory("BModule", "global::Lib.Clock", "CreateB"))
							.Because("each factory has a handle of its own besides the shared one, so the container calls both");
					}
				}

				[Test]
				[Arguments("same")]
				[Arguments("friend")]
				[Arguments("full")]
				[Arguments("reference")]
				public async Task WhenOneNamesTheClass_ConflictsAsInTheModulesOwnAssembly(string consumer)
				{
					const string container = """
						[Container]
						[Import(typeof(AModule))]
						[Import(typeof(BModule))]
						[Import(typeof(CModule))]
						public static partial class {0};
						""";
					GeneratorResult result = Run(ThreeModules.Replace("{0}", "Singleton"), container, consumer);
					string expected = Run(ThreeModules.Replace("{0}", "Singleton"), container, "same").Diagnostics.Single();

					await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT111") && Message(diagnostic) == Message(expected))
						.Because("CModule constructs the class, so the factories of the other modules are further productions of its one instance, as in the modules' own assembly");

					static string Message(string diagnostic)
						=> diagnostic.Substring(diagnostic.IndexOf("AWT111", StringComparison.Ordinal));
				}

				[Test]
				[Arguments("same")]
				[Arguments("friend")]
				[Arguments("full")]
				[Arguments("reference")]
				public async Task TwoFactoriesOfOneModule_BesideAModuleThatConstructsTheClass_ReportOnlyTheConflict(string consumer)
				{
					const string library = """
						using Awaiten;

						namespace Lib;

						internal sealed class TimeSource { }
						public sealed class Clock { public Clock() { } }

						[Module]
						[Singleton<TimeSource>]
						[Singleton<Clock>(Factory = nameof(CreateA))]
						[Singleton<Clock>(Factory = nameof(CreateB))]
						public static partial class ClockModule
						{
						    internal static Clock CreateA(TimeSource source) => new();
						    internal static Clock CreateB(TimeSource source) => new();
						}

						[Module]
						[Singleton<Clock>]
						public static partial class OtherModule;
						""";
					const string container = """
						[Container]
						[Import(typeof(ClockModule))]
						[Import(typeof(OtherModule))]
						public static partial class {0};
						""";
					GeneratorResult result = Run(library, container, consumer);

					await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT111") && diagnostic.Contains("factory 'Lib.ClockModule.CreateA'"))
						.Because("the module calls each factory through a handle of its own, so no factory falls back to a call of its internal method (AWT108) and only the conflict of the one instance remains");
				}

				[Test]
				[Arguments("same")]
				[Arguments("friend")]
				[Arguments("full")]
				[Arguments("reference")]
				public async Task TwoFactoriesOfOneModule_BesideAModuleThatBuildsTheClassThroughItsHandle_Conflict(string consumer)
				{
					const string library = """
						using Awaiten;

						namespace Lib;

						internal sealed class TimeSource { }
						public sealed class Clock { internal Clock(TimeSource source) { } }

						[Module]
						[Singleton<TimeSource>]
						[Singleton<Clock>(Factory = nameof(CreateA))]
						[Singleton<Clock>(Factory = nameof(CreateB))]
						public static partial class ClockModule
						{
						    internal static Clock CreateA(TimeSource source) => new(source);
						    internal static Clock CreateB(TimeSource source) => new(source);
						}

						[Module]
						[Singleton<Clock>]
						public static partial class OtherModule;

						[Module]
						[Singleton<Clock>(Factory = nameof(CreateC))]
						public static partial class ThirdModule
						{
						    internal static Clock CreateC(TimeSource source) => new(source);
						}
						""";
					const string container = """
						[Container]
						[Import(typeof(ClockModule))]
						[Import(typeof(OtherModule))]
						public static partial class {0};
						""";
					GeneratorResult result = Run(library, container, consumer);
					string expected = Run(library, container, "same").Diagnostics.Single();

					await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT111") && Message(diagnostic) == Message(expected))
						.Because("the other module constructs the class, through its handle as a third module builds the class through one, so the factories are productions of its one instance, as in the modules' own assembly, though the module has no handle the other module's could coalesce with");

					static string Message(string diagnostic)
						=> diagnostic.Substring(diagnostic.IndexOf("AWT111", StringComparison.Ordinal));
				}
			}
		}

		[Test]
		public async Task ConsumerGrantedInternalsVisibleTo_ConstructsDirectly()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TestAssembly")]

				namespace Lib;

				public interface IGrinder { }
				internal sealed class BeanStore { }
				internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

				[Module]
				[Singleton<BeanStore>]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }
				""", Container);
			string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

			await That(result.Diagnostics).IsEmpty();
			await That(source).Contains("new global::Lib.BeanStore()").And.DoesNotContain("Hidden_")
				.Because("a container that can name the internal service constructs it itself and ignores the handles");
		}
	}

	public class ScanMatchesTakingAnInternalService
	{
		private const string Library = """
			using Awaiten;

			namespace Lib;

			public interface IPlugin { }
			internal interface IStore { }
			internal sealed class Store : IStore { }
			internal sealed class Roaster : IPlugin { public Roaster(IStore store) { } }

			[Module]
			[Singleton<Store, IStore>]
			[Scan<IPlugin>(As = ScanAs.Marker)]
			public static partial class CoffeeModule
			{
			    private sealed class Plugin : IPlugin { public Plugin(IStore store) { } }
			}
			""";

		private const string Container = """
			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class MyContainer;
			""";

		[Test]
		public async Task ContainerInTheModulesAssembly_RegistersEveryMatch()
		{
			GeneratorResult result = Generator.Run(Library + Container);
			string source = result.Sources["Awaiten.Lib.MyContainer.g.cs"];

			await That(result.Diagnostics).IsEmpty();
			await That(source).Contains("new global::Lib.Roaster(")
				.Because("the container constructs the internal match itself, with the internal store it builds");
			await That(source).Contains("global::Lib.CoffeeModule.Internals.InternalScan_Plugin_")
				.Because("the container builds the private match through the internal factory that takes the store directly");
			await That(ModuleSource(result)).Contains("internal static global::Lib.IPlugin InternalScan_Plugin_")
				.And.Contains($"public static global::Lib.IPlugin Scan_Plugin_")
				.Because("a container in another assembly still takes the store through its handle");
		}

		[Test]
		public async Task ContainerInAnotherAssembly_RegistersEveryMatch()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("Internals.Scan_Plugin_").And.Contains("Internals.Scan_Roaster_");
		}
	}

	public class InternalImplementationOfSeveralModules
	{
		private const string Library = """
			using Awaiten;

			namespace Lib;

			public interface IA { }
			public interface IB { }
			internal sealed class Impl : IA, IB { }

			[Module]
			[Singleton<Impl, IA>]
			public static partial class AModule { }

			[Module]
			[Singleton<Impl, IB>]
			public static partial class BModule { }
			""";

		private const string Container = """
			[Container]
			[Import(typeof(AModule))]
			[Import(typeof(BModule))]
			public static partial class MyContainer;
			""";

		[Test]
		public async Task RegisteredUnderOnePublicServiceEach_IsOneInstanceInAContainerOfAnotherAssembly()
		{
			GeneratorResult same = Generator.Run(Library + Container);
			GeneratorResult cross = Generator.RunWithGeneratedReferencedAssembly(Library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container);

			await That(same.Diagnostics).IsEmpty();
			await That(cross.Diagnostics).IsEmpty()
				.Because("both modules build the implementation into one instance handle, as a container in their own assembly builds one instance");
			await That(cross.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains(HiddenFactory("global::Lib.Impl"))
				.And.DoesNotContain("Internals." + Factory("global::Lib.Impl"));
		}
	}

	public class OneImplementationUnderTwoInternalServices
	{
		private static string Library(string lifetime, bool nested, bool disposable, bool grant = false) => $$"""
			using Awaiten;

			{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

			namespace Lib;

			public interface IPub { }
			internal interface IA { }
			internal interface IB { }
			{{(nested ? "" : Impl(disposable))}}
			internal sealed class R : IPub { public R(IA a, IB b) { } }

			[Module]
			[{{lifetime}}<{{(nested ? "CoffeeModule.Impl" : "Impl")}}, IA>]
			[{{lifetime}}<{{(nested ? "CoffeeModule.Impl" : "Impl")}}, IB>]
			[Transient<R, IPub>]
			public static partial class CoffeeModule
			{
			    {{(nested ? Impl(disposable).Replace("internal sealed", "private sealed") : "")}}
			}
			""";

		private static string Impl(bool disposable)
			=> disposable
				? "internal sealed class Impl : IA, IB, System.IDisposable { public void Dispose() { } }"
				: "internal sealed class Impl : IA, IB { }";

		[Test]
		[Arguments("Singleton", false, false)]
		[Arguments("Transient", false, true)]
		[Arguments("Singleton", true, false)]
		public async Task TheLibrary_BuildsTheImplementationOnce(string lifetime, bool nested, bool disposable)
		{
			GeneratorResult result = RunLibrary(Library(lifetime, nested, disposable));
			string implementation = nested ? "global::Lib.CoffeeModule.Impl" : "global::Lib.Impl";
			string module = ModuleSource(result);

			await That(result.Diagnostics).DoesNotContain("*CS0111*").AsWildcard()
				.Because("the public registration needs both internal services in the same round, which must not build their one implementation twice");
			await That(module.Split([$"{HiddenFactory(implementation)}(",], StringSplitOptions.None).Length).IsEqualTo(2)
				.Because("the module declares one handle factory for the implementation");
		}

		[Test]
		[Arguments("Singleton", false)]
		[Arguments("Transient", true)]
		public async Task EveryContainer_ResolvesItWithoutDiagnostics(string lifetime, bool disposable)
		{
			const string container = """
				[Container]
				[Import(typeof(CoffeeModule))]
				public static partial class MyContainer;
				""";
			const string consumer = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + container;

			await That(Generator.Run(Library(lifetime, false, disposable) + container).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Library(lifetime, false, disposable), consumer).Diagnostics).IsEmpty()
				.Because("a container in another assembly takes both services through the one instance handle");
			await That(Generator.RunWithGeneratedReferencedAssembly(Library(lifetime, false, disposable, grant: true), consumer).Diagnostics).IsEmpty()
				.Because("a friend constructs the implementation itself");
		}
	}

	public class PrivateImplementationTakingAnInternalService
	{
		private static string Library(bool grant) => $$"""
			using Awaiten;

			{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

			namespace Lib;

			public interface IGrinder { object Store { get; } }
			internal interface IStore { }
			internal sealed class Store : IStore { }

			[Module]
			[Singleton<Store, IStore>]
			[Singleton<CoffeeModule.Grinder, IGrinder>]
			public static partial class CoffeeModule
			{
			    private sealed class Grinder : IGrinder { public Grinder(IStore store) => Store = store; public object Store { get; } }
			}
			""";

		private const string Container = """
			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class MyContainer;
			""";

		[Test]
		public async Task ContainerInTheModulesAssembly_BuildsItThroughAnInternalFactory()
		{
			GeneratorResult result = Generator.Run(Library(grant: false) + Container);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.Lib.MyContainer.g.cs"]).Contains($"global::Lib.CoffeeModule.Internals.Internal{Factory("global::Lib.CoffeeModule.Grinder")}(")
				.Because("a container that can name the internal service calls the factory that takes it directly, since it registers no handle");
			await That(ModuleSource(result)).Contains($"internal static global::Lib.IGrinder Internal{Factory("global::Lib.CoffeeModule.Grinder")}(global::Lib.IStore @store)")
				.And.Contains($"public static global::Lib.IGrinder {Factory("global::Lib.CoffeeModule.Grinder")}({Handle("global::Lib.IStore")} @store)")
				.Because("an assembly that cannot name the internal service still takes it through its handle");
		}

		[Test]
		public async Task ContainerInAnAssemblyGrantedInternalsVisibleTo_BuildsItThroughTheInternalFactory()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(grant: true), "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container);

			await That(result.Diagnostics).IsEmpty()
				.Because("a friend constructs the store itself and registers no handle, so it calls the factory that takes the store directly");
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains($"Internal{Factory("global::Lib.CoffeeModule.Grinder")}(");
		}

		[Test]
		public async Task ContainerInAnotherAssembly_TakesItThroughTheHandle()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(grant: false), "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).DoesNotContain($"Internal{Factory("global::Lib.CoffeeModule.Grinder")}")
				.Because("the internal factory is not accessible to an assembly without InternalsVisibleTo");
		}

		[Test]
		public async Task HookTakingTheInternalService_ReportsAwt203()
		{
			GeneratorResult result = Generator.Run(Library(grant: false)
				.Replace("[Singleton<CoffeeModule.Grinder, IGrinder>]", "[Singleton<CoffeeModule.Grinder, IGrinder>(OnActivated = nameof(Activate))]")
				.Replace("public object Store { get; } }", "public object Store { get; } }\n    private static void Activate(Grinder grinder, IStore store) { }"));

			await That(result.Diagnostics).Contains("*AWT203*").AsWildcard()
				.Because("the one wrapper of the hook serves the containers that register no handles too, so it cannot take the internal service through its handle");
		}
	}

	public class PrivateImplementationOfAnInternalService
	{
		private const string Consumed = "";

		private const string Unconsumed = "unconsumed";

		private const string Eager = "(Eager = true)";

		private const string Hooked = "(OnActivated = nameof(Activate), OnRelease = nameof(Release))";

		private const string Container = """
			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class MyContainer;
			""";

		private static string Library(string variant, bool grant = false) => $$"""
			using Awaiten;

			{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

			namespace Lib;

			public interface IGrinder { object Store { get; } }
			internal interface IBeanStore { }
			internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) => Store = store; public object Store { get; } }

			[Module]
			[Singleton<CoffeeModule.BeanStore, IBeanStore>{{(variant is Eager or Hooked ? variant : "")}}]
			{{(variant == Unconsumed || variant == Eager ? "" : "[Singleton<Grinder, IGrinder>]")}}
			public static partial class CoffeeModule
			{
			    private sealed class BeanStore : IBeanStore { }
			    private static void Activate(BeanStore store) { }
			    private static void Release(BeanStore store) { }
			}
			""";

		[Test]
		[Arguments(Consumed)]
		[Arguments(Unconsumed)]
		[Arguments(Eager)]
		[Arguments(Hooked)]
		public async Task Library_BuildsCleanly(string variant)
		{
			GeneratorResult result = Generator.Run(Library(variant));

			await That(result.Diagnostics).IsEmpty()
				.Because("no public generated member names the internal service or the private implementation");
			await That(ModuleSource(result)).DoesNotContain("public static global::Lib.IBeanStore")
				.And.DoesNotContain("global::Lib.CoffeeModule.BeanStore Value")
				.Because("the factory of the private implementation is internal, and its instance handle never exposes the private type");
		}

		[Test]
		[Arguments(Consumed)]
		[Arguments(Unconsumed)]
		[Arguments(Eager)]
		[Arguments(Hooked)]
		public async Task LibraryGrantingInternalsVisibleTo_BuildsCleanly(string variant)
		{
			GeneratorResult result = Generator.Run(Library(variant, grant: true));

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		[Arguments(Consumed)]
		[Arguments(Eager)]
		[Arguments(Hooked)]
		public async Task ContainerInTheModulesAssembly_BuildsItThroughTheInternalFactory(string variant)
		{
			GeneratorResult result = Generator.Run(Library(variant) + Container);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.Lib.MyContainer.g.cs"]).Contains($"global::Lib.CoffeeModule.Internals.Internal{Factory("global::Lib.CoffeeModule.BeanStore")}()")
				.Because("a container that builds the internal services itself calls the internal factory of the private implementation");
		}

		[Test]
		[Arguments(Consumed)]
		[Arguments(Eager)]
		[Arguments(Hooked)]
		public async Task ContainerInAnAssemblyGrantedInternalsVisibleTo_BuildsItThroughTheInternalFactory(string variant)
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(variant, grant: true), "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container);
			string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

			await That(result.Diagnostics).IsEmpty();
			await That(source).Contains($"Internal{Factory("global::Lib.CoffeeModule.BeanStore")}()").And.DoesNotContain("Hidden")
				.Because("a friend builds the internal services itself, through the internal factory of the private implementation, and registers no handles");
		}

		[Test]
		[Arguments(Consumed)]
		[Arguments(Eager)]
		[Arguments(Hooked)]
		public async Task ContainerInAnotherAssembly_TakesItThroughTheHandle(string variant)
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(variant), "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains(HiddenFactory("global::Lib.CoffeeModule.BeanStore"))
				.Because("a container that cannot name the internal service builds the private implementation into its instance handle");
		}
	}

	public class PrivateProductionMemberOfAnInternalService
	{
		private const string Instance = "Instance = nameof(Default)";

		private const string InstanceProperty = "Instance = nameof(DefaultProperty)";

		private const string Factory = "Factory = nameof(Make)";

		private const string AsyncFactory = "Factory = nameof(MakeAsync)";

		private const string FactoryTakingAnInternalService = "Factory = nameof(MakeWithHeater)";

		private const string ImplementationFactory = "Factory = nameof(MakeKettle)";

		private const string Container = """
			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class MyContainer;
			""";

		private const string Consumer = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container;

		private static string Library(string production, bool grant = false, string service = "IKettle") => $$"""
			using System.Threading.Tasks;
			using Awaiten;

			{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

			namespace Lib;

			public interface IUser { }
			public interface IPublicKettle { }
			internal interface IKettle : IPublicKettle { }
			internal interface IHeater { }
			internal sealed class Heater : IHeater { }
			internal sealed class Kettle : IKettle { }
			internal sealed class User : IUser { public User({{service}} kettle) { } }

			[Module]
			[{{(production == ImplementationFactory ? "Singleton<Kettle, IKettle>" : $"Singleton<{service}>")}}({{production}})]
			[Singleton<Heater, IHeater>]
			[Singleton<User, IUser>]
			public static partial class CoffeeModule
			{
			    private static readonly {{service}} Default = new Kettle();
			    private static {{service}} DefaultProperty { get; } = new Kettle();
			    private static {{service}} Make() => new Kettle();
			    private static Task<{{service}}> MakeAsync() => Task.FromResult<{{service}}>(new Kettle());
			    private static {{service}} MakeWithHeater(IHeater heater) => new Kettle();
			    private static Kettle MakeKettle() => new Kettle();
			}
			""";

		private static string Wrapper(string production)
		{
			string member = production.Substring(production.IndexOf('(') + 1).TrimEnd(')');
			string produced = production == ImplementationFactory ? "global::Lib.Kettle" : "global::Lib.IKettle";
			return $"{(production.StartsWith("Instance") ? "Instance" : "Factory")}_{member}_{Fnv1a($"{member}\0{produced}"):x8}";
		}

		[Test]
		[Arguments(Instance)]
		[Arguments(InstanceProperty)]
		[Arguments(Factory)]
		[Arguments(AsyncFactory)]
		[Arguments(FactoryTakingAnInternalService)]
		[Arguments(ImplementationFactory)]
		public async Task EveryContainer_BuildsItWithoutDiagnostics(string production)
		{
			await That(Generator.Run(Library(production)).Diagnostics).IsEmpty();
			await That(Generator.Run(Library(production, grant: true)).Diagnostics).IsEmpty();
			await That(Generator.Run(Library(production) + Container).Diagnostics).IsEmpty()
				.Because("a container in the module's own assembly builds the internal service itself and calls the private member through its internal wrapper");
			await That(Generator.RunWithGeneratedReferencedAssembly(Library(production), Consumer).Diagnostics).IsEmpty()
				.Because("a container in another assembly takes the internal service through its handle");
			await That(Generator.RunWithGeneratedReferencedAssembly(Library(production, grant: true), Consumer).Diagnostics).IsEmpty()
				.Because("a friend builds the internal service itself and calls the private member through its internal wrapper");
		}

		[Test]
		[Arguments(Instance)]
		[Arguments(InstanceProperty)]
		[Arguments(Factory)]
		[Arguments(AsyncFactory)]
		[Arguments(FactoryTakingAnInternalService)]
		[Arguments(ImplementationFactory)]
		public async Task Library_EmitsAnInternalWrapper(string production)
		{
			GeneratorResult result = Generator.Run(Library(production));

			string returnType = production switch
			{
				ImplementationFactory => "global::Lib.Kettle",
				AsyncFactory => "global::System.Threading.Tasks.Task<global::Lib.IKettle>",
				_ => "global::Lib.IKettle",
			};

			await That(result.Diagnostics).IsEmpty();
			await That(ModuleSource(result)).Contains($"internal static {returnType} {Wrapper(production)}")
				.Because("only the containers that can name the internal service may call the wrapper of the private member");
		}

		[Test]
		[Arguments(Instance)]
		[Arguments(Factory)]
		[Arguments(AsyncFactory)]
		[Arguments(FactoryTakingAnInternalService)]
		public async Task ContainerInTheModulesAssembly_CallsTheInternalWrapper(string production)
		{
			GeneratorResult result = Generator.Run(Library(production) + Container);

			await That(result.Sources["Awaiten.Lib.MyContainer.g.cs"]).Contains($"global::Lib.CoffeeModule.Internals.{Wrapper(production)}");
		}

		[Test]
		[Arguments(Instance)]
		[Arguments(Factory)]
		[Arguments(AsyncFactory)]
		[Arguments(FactoryTakingAnInternalService)]
		public async Task ContainerInAnAssemblyGrantedInternalsVisibleTo_CallsTheInternalWrapper(string production)
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(production, grant: true), Consumer);
			string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

			await That(source).Contains($"global::Lib.CoffeeModule.Internals.{Wrapper(production)}").And.DoesNotContain("Hidden")
				.Because("a friend builds the internal service itself and registers no handles");
		}

		[Test]
		[Arguments(Instance)]
		[Arguments(Factory)]
		public async Task ContainerInAnotherAssembly_TakesItThroughTheHandle(string production)
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(production), Consumer);

			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("Internals.HiddenRegistration_").And.DoesNotContain(Wrapper(production))
				.Because("the internal wrapper is not accessible to an assembly without InternalsVisibleTo");
		}

		[Test]
		public async Task KeyedAndHooked_EveryContainerBuildsItWithoutDiagnostics()
		{
			string Keyed(bool grant) => Library(Factory, grant)
				.Replace($"[Singleton<IKettle>({Factory})]", $"[Singleton<IKettle>({Factory}, Key = \"hot\", OnActivated = nameof(Heat), OnRelease = nameof(Cool))]\n[Singleton<IKettle>({Factory})]")
				.Replace("public User(IKettle kettle)", "public User([FromKey(\"hot\")] IKettle kettle, IKettle plain)")
				.Replace("private static Kettle MakeKettle()", "internal static void Heat(IKettle kettle) { }\n    internal static Task Cool(IKettle kettle) => Task.CompletedTask;\n    private static Kettle MakeKettle()");

			await That(Generator.Run(Keyed(grant: false)).Diagnostics).IsEmpty();
			await That(Generator.Run(Keyed(grant: false) + Container).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Keyed(grant: false), Consumer).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Keyed(grant: true), Consumer).Diagnostics).IsEmpty()
				.Because("the key and the internal hooks travel with the registration, whichever container calls the private factory");
		}

		[Test]
		public async Task OverloadOfAnotherService_EveryContainerBuildsItWithoutDiagnostics()
		{
			string Overloaded(bool grant) => Library(Factory, grant)
				.Replace("[Singleton<Heater, IHeater>]", "[Singleton<IHeater>(Factory = nameof(Make))]")
				.Replace("private static IKettle Make()", "internal static IHeater Make(IKettle kettle) => new Heater();\n    private static IKettle Make()")
				.Replace("public User(IKettle kettle)", "public User(IKettle kettle, IHeater heater)");

			await That(Generator.Run(Overloaded(grant: false)).Diagnostics).IsEmpty();
			await That(ModuleSource(Generator.Run(Overloaded(grant: false)))).Contains($"internal static global::Lib.IKettle {Wrapper(Factory)}()")
				.Because("the internal overload produces another service, so the private factory is still the only one of its service");
			await That(Generator.Run(Overloaded(grant: true)).Diagnostics).IsEmpty();
			await That(Generator.Run(Overloaded(grant: false) + Container).Diagnostics).IsEmpty()
				.Because("a container in the module's own assembly calls the internal overload for its service and the internal wrapper for the other");
			await That(Generator.RunWithGeneratedReferencedAssembly(Overloaded(grant: false), Consumer).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Overloaded(grant: true), Consumer).Diagnostics).IsEmpty()
				.Because("a friend calls the internal overload for its service and the internal wrapper for the other");
		}

		[Test]
		public async Task OverloadOfAPublicService_GetsItsOwnPublicWrapper()
		{
			string Overloaded(bool grant) => Library(Factory, grant)
				.Replace("[Singleton<User, IUser>]", "[Singleton<User, IUser>]\n[Singleton<IPublicKettle>(Factory = nameof(Make))]")
				.Replace("private static IKettle Make()", "internal static IPublicKettle Make(IUser user) => new Kettle();\n    private static IKettle Make()");

			string module = ModuleSource(Generator.Run(Overloaded(grant: false)));

			await That(module).Contains($"internal static global::Lib.IKettle {Wrapper(Factory)}()")
				.And.Contains($"public static global::Lib.IPublicKettle Factory_Make_{Fnv1a("Make\0global::Lib.IPublicKettle"):x8}(global::Lib.IUser @user)")
				.Because("the wrappers of one member name differ by the service they produce, so the internal and the public one never collide");
			await That(Generator.Run(Overloaded(grant: false) + Container).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Overloaded(grant: false), Consumer).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Overloaded(grant: true), Consumer).Diagnostics).IsEmpty();
		}

		[Test]
		[Arguments(Factory)]
		[Arguments(ImplementationFactory)]
		public async Task PrivateHook_IsReportedInTheLibraryBuild(string production)
		{
			GeneratorResult result = Generator.Run(Library(production)
				.Replace($"({production})]", $"({production}, OnActivated = nameof(Heat))]")
				.Replace("private static Kettle MakeKettle()", "private static void Heat(IKettle kettle) { }\n    private static Kettle MakeKettle()"));

			await That(result.Diagnostics).Contains("*AWT164*'Heat'*").AsWildcard()
				.Because("a hook of a type the module's assembly can name must be reachable from that assembly, so no container calls a private one");
		}

		[Test]
		[Arguments(Instance, "Default")]
		[Arguments(Factory, "Make")]
		public async Task UnderAPublicService_IsCalledThroughAPublicWrapperByEveryContainer(string production, string member)
		{
			await That(Generator.Run(Library(production, service: "IPublicKettle")).Diagnostics).IsEmpty();
			await That(ModuleSource(Generator.Run(Library(production, service: "IPublicKettle")))).Contains($"public static global::Lib.IPublicKettle {(production == Instance ? "Instance" : "Factory")}_{member}_")
				.Because("no container could call a private member of a public service, so the module wraps it publicly for all of them");
			await That(Generator.Run(Library(production, service: "IPublicKettle") + Container).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Library(production, service: "IPublicKettle"), Consumer).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Library(production, service: "IPublicKettle", grant: true), Consumer).Diagnostics).IsEmpty();
		}
	}

	public class PrivateTypeInTheSignatureOfAPrivateMember
	{
		private const string Container = """
			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class MyContainer;
			""";

		private const string Consumer = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container;

		private static string Library(string registration, string members, bool grant = false) => $$"""
			using System.Threading.Tasks;
			using Awaiten;

			{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

			namespace Lib;

			public interface IUser { }
			internal interface IKettle { }
			internal interface IWarm { Task WarmAsync(); }
			internal sealed class User : IUser { public User(IKettle kettle) { } }

			[Module]
			[AsyncInitializer<IWarm>(nameof(IWarm.WarmAsync))]
			[{{registration}}]
			[Singleton<User, IUser>]
			public static partial class CoffeeModule
			{
			    private sealed class Impl : IKettle { }
			    private sealed class WarmImpl : IKettle, IWarm { public Task WarmAsync() => Task.CompletedTask; }
			    {{members}}
			}
			""";

		private static string Wrapper(string kind, string member)
			=> $"{kind}_{member}_{Fnv1a($"{member}\0global::Lib.IKettle"):x8}";

		[Test]
		[Arguments("Factory = nameof(Make)", "private static Impl Make() => new Impl();", "internal static global::Lib.IKettle Factory_Make_{0}() => Make();")]
		[Arguments("Factory = nameof(Make)", "private static async Task<Impl> Make() { await Task.Yield(); return new Impl(); }", "internal static async global::System.Threading.Tasks.Task<global::Lib.IKettle> Factory_Make_{0}() => await Make().ConfigureAwait(false);")]
		[Arguments("Instance = nameof(Make)", "private static readonly Impl Make = new();", "internal static global::Lib.IKettle Instance_Make_{0} => Make;")]
		public async Task ReturnType_EveryContainerBuildsItThroughAWrapperReturningTheRegisteredType(string production, string members, string wrapper)
		{
			string library = Library($"Singleton<IKettle>({production})", members);
			string granted = Library($"Singleton<IKettle>({production})", members, grant: true);
			string kind = production.StartsWith("Instance") ? "Instance" : "Factory";

			await That(Generator.Run(library).Diagnostics).IsEmpty();
			await That(ModuleSource(Generator.Run(library))).Contains(string.Format(wrapper, Fnv1a("Make\0global::Lib.IKettle").ToString("x8")))
				.Because("no container can name the private type, so the wrapper returns the registered type");
			await That(Generator.Run(granted).Diagnostics).IsEmpty();
			await That(Generator.Run(library + Container).Diagnostics).IsEmpty();
			await That(Generator.Run(library + Container).Sources["Awaiten.Lib.MyContainer.g.cs"]).Contains($"global::Lib.CoffeeModule.Internals.{Wrapper(kind, "Make")}");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, Consumer).Diagnostics).IsEmpty()
				.Because("a container in another assembly takes the internal service through its handle");
			await That(Generator.RunWithGeneratedReferencedAssembly(granted, Consumer).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(granted, Consumer).Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains($"global::Lib.CoffeeModule.Internals.{Wrapper(kind, "Make")}")
				.Because("a friend calls the wrapper like a container in the module's own assembly");
		}

		[Test]
		public async Task ReturnTypeInitializedByAConventionTheRegisteredTypeIsNot_IsReportedInTheLibraryBuild()
		{
			string library = Library("Singleton<IKettle>(Factory = nameof(Make))", "private static WarmImpl Make() => new WarmImpl();");
			const string expected = "*AWT234*'Lib.CoffeeModule.WarmImpl', which the module's factory 'Make' returns,*'Lib.IWarm.WarmAsync'*private to the module*registered type 'Lib.IKettle'*";

			await That(Generator.Run(library).Diagnostics).HasSingle().Which.Contains(expected).AsWildcard()
				.Because("every container sees the factory return the registered type, so the convention would silently never run");
			await That(Generator.Run(Library("Singleton<IKettle>(Factory = nameof(Make))", "private static WarmImpl Make() => new WarmImpl();", grant: true)).Diagnostics).HasSingle().Which.Contains(expected).AsWildcard();
			await That(Generator.Run(library + Container).Diagnostics).HasSingle().Which.Contains(expected).AsWildcard()
				.Because("the module reports it once, so the container in its assembly adds nothing");
		}

		[Test]
		[Arguments("Singleton<CoffeeModule.Impl, IKettle>(Factory = nameof(Make))", "private static Impl Make() => new Impl();", "Factory")]
		[Arguments("Singleton<CoffeeModule.Impl, IKettle>(Instance = nameof(Make))", "private static readonly Impl Make = new();", "Instance")]
		public async Task RegisteredType_IsReportedInTheLibraryBuild(string registration, string members, string kind)
		{
			string expected = $"*AWT216*'Lib.CoffeeModule.Impl' cannot be registered*only the module can name it*[Singleton<Lib.IKettle>({kind} = nameof(Make))]*";

			await That(Generator.Run(Library(registration, members)).Diagnostics).HasSingle().Which.Contains(expected).AsWildcard()
				.Because("no container can name the private type it would hold the instance as, wherever it imports the module");
			await That(Generator.Run(Library(registration, members, grant: true)).Diagnostics).HasSingle().Which.Contains(expected).AsWildcard();
			await That(Generator.Run(Library(registration, members) + Container).Diagnostics).HasSingle().Which.Contains(expected).AsWildcard()
				.Because("the module reports it once, so the container in its assembly adds nothing");
		}

		[Test]
		[Arguments(ReportDiagnostic.Warn)]
		[Arguments(ReportDiagnostic.Suppress)]
		public async Task RegisteredType_LoweredBelowAnError_LeavesTheContainerInTheModulesAssemblyNoShell(ReportDiagnostic severity)
		{
			string library = Library("Singleton<CoffeeModule.Impl, IKettle>(Factory = nameof(Make))", "private static Impl Make() => new Impl();")
				.Replace("[Singleton<User, IUser>]", "");

			GeneratorResult lowered = Generator.Run(library + Container, new Dictionary<string, ReportDiagnostic> { ["AWT216"] = severity, });
			GeneratorResult error = Generator.Run(library + Container, new Dictionary<string, ReportDiagnostic>());

			await That(lowered.Sources["Awaiten.Lib.MyContainer.g.cs"]).DoesNotContain("has registration errors")
				.Because("the library build no longer fails, so a shell would compile and throw only at run time");
			await That(error.Sources["Awaiten.Lib.MyContainer.g.cs"]).Contains("has registration errors")
				.Because("the module's error fails the build, and the graph lacks the registration it rejects");
		}

		[Test]
		public async Task Parameter_IsReportedInTheLibraryBuildWhetherOrNotItGrantsInternalsVisibleTo()
		{
			const string registration = "Singleton<IKettle>(Factory = nameof(Make))";
			const string members = "private static IKettle Make(Impl impl) => impl;";
			const string expected = "*AWT195*'Lib.IKettle' is registered through the module, but the parameter 'impl' of its factory 'Make' needs 'Lib.CoffeeModule.Impl', which is private to the module*";

			await That(Generator.Run(Library(registration, members)).Diagnostics).Contains(expected).AsWildcard();
			await That(Generator.Run(Library(registration, members)).Diagnostics).Contains("*AWT195*'impl'*").AsWildcard().Exactly(1)
				.Because("the module reports it once, though its handle reports the same parameter");
			await That(Generator.Run(Library(registration, members, grant: true)).Diagnostics).HasSingle().Which.Contains(expected).AsWildcard()
				.Because("no container can resolve a type private to the module, so a friend cannot either, and a friend reaches no handle");
			await That(Generator.Run(Library(registration, members) + Container).Diagnostics).IsEqualTo(Generator.Run(Library(registration, members)).Diagnostics)
				.Because("the module reports it, so the container in its assembly adds nothing");
		}
	}

	public class PrivateParameterOfAPrivateImplementation
	{
		private static string Library(bool grant = false) => $$"""
			using Awaiten;

			{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

			namespace Lib;

			internal interface IFoo { }
			public interface IUser { }
			internal sealed class User : IUser { public User(IFoo foo) { } }

			[Module]
			[Singleton<CoffeeModule.Impl, IFoo>]
			[Singleton<User, IUser>]
			public static partial class CoffeeModule
			{
			    private sealed class Other { }
			    private sealed class Impl : IFoo { public Impl(Other other) { } }
			}
			""";

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task IsReportedOnce(bool grant)
		{
			await That(Generator.Run(Library(grant)).Diagnostics).Contains("*AWT195*'Lib.CoffeeModule.Impl' is self-compiled*constructor parameter 'other' needs 'Lib.CoffeeModule.Other'*").AsWildcard().Exactly(1);
			await That(Generator.Run(Library(grant)).Diagnostics).Contains("*AWT195*'Lib.CoffeeModule.Other'*").AsWildcard().Exactly(1)
				.Because("the module's factory and the service's handle reject the same parameter, which is one problem");
		}
	}

	public class PrivateMemberOfAnInternalModule
	{
		private const string Container = """
			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class MyContainer;
			""";

		private const string Consumer = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container;

		private static string Library(string service, string production, string member) => $$"""
			using Awaiten;

			[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TestAssembly")]

			namespace Lib;

			{{service}} interface IKettle { }
			internal sealed class Kettle : IKettle { }

			[Module]
			[Singleton<IKettle>({{production}})]
			internal static partial class CoffeeModule
			{
			    {{member}}
			}
			""";

		[Test]
		[Arguments("internal", "Factory = nameof(Make)", "private static IKettle Make() => new Kettle();")]
		[Arguments("internal", "Instance = nameof(Shared)", "private static readonly IKettle Shared = new Kettle();")]
		[Arguments("public", "Factory = nameof(Make)", "private static IKettle Make() => new Kettle();")]
		[Arguments("public", "Instance = nameof(Shared)", "private static readonly IKettle Shared = new Kettle();")]
		public async Task EveryContainer_CallsItThroughAnInternalWrapper(string service, string production, string member)
		{
			string library = Library(service, production, member);
			string kind = production.StartsWith("Instance") ? "Instance" : "Factory";

			await That(Generator.Run(library).Diagnostics).IsEmpty();
			await That(Generator.Run(library + Container).Diagnostics).IsEmpty()
				.Because("the container in the module's assembly calls the internal wrapper instead of the private member");
			await That(Generator.Run(library + Container).Sources["Awaiten.Lib.MyContainer.g.cs"]).Contains($"global::Lib.CoffeeModule.Internals.{kind}_");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, Consumer).Diagnostics).IsEmpty()
				.Because("only the module's assembly and its friends can import an internal module, and each of them reaches an internal wrapper");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, Consumer).Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains($"global::Lib.CoffeeModule.Internals.{kind}_");
		}
	}

	public class HookOfAnotherType
	{
		private const string Container = """
			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class MyContainer;
			""";

		private static string Library(string service, string factory) => $$"""
			using Awaiten;

			namespace Lib;

			{{service}} interface IKettle { }
			internal interface IOther { }
			internal sealed class Kettle : IKettle { }
			public interface IUser { }
			internal sealed class User : IUser { public User(IKettle kettle) { } }

			[Module]
			[Singleton<IKettle>(Factory = nameof(Make), OnActivated = nameof(Heat))]
			[Singleton<User, IUser>]
			public static partial class CoffeeModule
			{
			    {{factory}} static IKettle Make() => new Kettle();
			    internal static void Heat(IOther other) { }
			}
			""";

		[Test]
		[Arguments("public", "internal")]
		[Arguments("internal", "private")]
		public async Task IsReportedOnceInEveryBuild(string service, string factory)
		{
			await That(Generator.Run(Library(service, factory)).Diagnostics).Contains("*AWT164*'Heat'*").AsWildcard().Exactly(1);
			await That(Generator.Run(Library(service, factory) + Container).Diagnostics).Contains("*AWT164*'Heat'*").AsWildcard().Exactly(1)
				.Because("the container in the module's assembly reports the hook itself, so the module leaves it to it");
		}
	}

	public class FactoryMemberConventions
	{
		private const string Container = """
			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class MyContainer;
			""";

		private const string Consumer = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container;

		private static string Library(string accessibility, bool grant = false) => $$"""
			using System.Threading.Tasks;
			using Awaiten;

			{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

			namespace Lib;

			public interface IUser { }
			internal interface IKettle { }
			internal sealed class Kettle : IKettle { public Task WarmAsync() => Task.CompletedTask; }
			internal sealed class User : IUser { public User(IKettle kettle) { } }

			[Module]
			[AsyncInitializer<Kettle>(nameof(Kettle.WarmAsync))]
			[Singleton<IKettle>(Factory = nameof(Make))]
			[Singleton<User, IUser>]
			public static partial class CoffeeModule
			{
			    {{accessibility}} static Kettle Make() => new Kettle();
			}
			""";

		[Test]
		[Arguments("private")]
		[Arguments("internal")]
		public async Task EveryContainer_RunsTheConventionsOfTheTypeTheFactoryReturns(string accessibility)
		{
			const string initializer = "((global::Lib.Kettle)created).WarmAsync()";

			await That(Generator.Run(Library(accessibility)).Diagnostics).IsEmpty();
			await That(Generator.Run(Library(accessibility, grant: true)).Diagnostics).IsEmpty();
			await That(Generator.Run(Library(accessibility) + Container).Sources["Awaiten.Lib.MyContainer.g.cs"]).Contains(initializer);
			await That(ModuleSource(Generator.Run(Library(accessibility)))).Contains("=> ((global::Lib.Kettle)@awaiten__instance.Value).WarmAsync();")
				.Because("the handle's initializer runs the convention of the type the factory returns, not of the registered service");
			await That(Generator.RunWithGeneratedReferencedAssembly(Library(accessibility), Consumer).Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("Internals.HiddenInitializer_IKettle_")
				.Because("a container in another assembly runs the convention through the handle's initializer");
			await That(Generator.RunWithGeneratedReferencedAssembly(Library(accessibility, grant: true), Consumer).Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains(initializer);
		}
	}

	public class UnconsumedInternalService
	{
		private const string Container = """
			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class MyContainer;
			""";

		private const string Consumer = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container;

		private static string Library(string production, string member, bool grant = false) => $$"""
			using Awaiten;

			{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

			namespace Lib;

			public interface IUser { }
			internal interface IKettle { }
			internal interface IHeater { }
			internal sealed class Kettle : IKettle { }
			internal sealed class Heater : IHeater { }
			internal sealed class User : IUser { public User(IKettle kettle) { } }

			[Module]
			[Singleton<Kettle, IKettle>]
			[Singleton<IHeater>({{production}})]
			[Singleton<User, IUser>]
			public static partial class CoffeeModule
			{
			    {{member}}
			}
			""";

		[Test]
		[Arguments("Factory = nameof(MakeHeater)", "internal static IHeater MakeHeater(IKettle kettle) => new Heater();")]
		[Arguments("Factory = nameof(MakeHeater)", "private static IHeater MakeHeater(IKettle kettle) => new Heater();")]
		[Arguments("Instance = nameof(Heater)", "internal static readonly IHeater Heater = new Lib.Heater();")]
		public async Task EveryContainer_BuildsWithoutDiagnostics(string production, string member)
		{
			await That(Generator.Run(Library(production, member)).Diagnostics).IsEmpty();
			await That(Generator.Run(Library(production, member, grant: true)).Diagnostics).IsEmpty();
			await That(Generator.Run(Library(production, member) + Container).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Library(production, member), Consumer).Diagnostics).IsEmpty()
				.Because("nothing the module exposes takes the internal service and the container cannot name it, so nothing there can resolve it");
			await That(Generator.RunWithGeneratedReferencedAssembly(Library(production, member, grant: true), Consumer).Diagnostics).IsEmpty();
		}

		[Test]
		public async Task ContainerInAnotherAssembly_LeavesItOut()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library("Factory = nameof(MakeHeater)", "internal static IHeater MakeHeater(IKettle kettle) => new Heater();"), Consumer);

			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).DoesNotContain("MakeHeater")
				.Because("the container could neither call the internal factory nor name what it produces");
		}

		[Test]
		public async Task EagerSingletonOfAModuleWithoutHandles_IsReportedByAContainerInAnotherAssembly()
		{
			string library = Library("Factory = nameof(MakeHeater), Eager = true", "internal static IHeater MakeHeater(IKettle kettle) => new Heater();", grant: true)
				.Replace("public static partial class CoffeeModule", "public static class CoffeeModule");

			await That(Generator.Run(library).Diagnostics).IsEmpty()
				.Because("the module leaves its internal services to the assemblies it grants InternalsVisibleTo");
			await That(Generator.RunWithGeneratedReferencedAssembly(library, Consumer).Diagnostics).IsEmpty()
				.Because("a friend builds the eager singleton itself");
			await That(Generator.RunWithGeneratedReferencedAssembly(library.Replace("\"TestAssembly\"", "\"OtherAssembly\""), Consumer).Diagnostics).Contains("*AWT231*'Lib.IHeater'*not partial*").AsWildcard()
				.Because("dropping the eager singleton would silently skip its construction");
		}
	}

	public class PublicOverloadOfAnotherType
	{
		private const string Container = """
			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class MyContainer;
			""";

		private const string Consumer = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container;

		private static string Library(bool grant = false) => $$"""
			using Awaiten;

			{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

			namespace Lib;

			public interface IKettle { }
			public interface IOther { }
			internal sealed class Kettle : IKettle, IOther { }

			[Module]
			[Singleton<IKettle>(Factory = nameof(Make), OnActivated = nameof(Heat))]
			public static partial class CoffeeModule
			{
			    internal static IKettle Make() => new Kettle();
			    public static IOther Make(IKettle kettle) => new Kettle();
			    internal static void Heat(IKettle kettle) { }
			    public static void Heat(IOther other) { }
			}
			""";

		[Test]
		public async Task Library_WrapsTheInternalFactoryAndHook()
		{
			string module = ModuleSource(Generator.Run(Library()));

			await That(module).Contains($"public static global::Lib.IKettle Factory_Make_{Fnv1a("Make\0global::Lib.IKettle"):x8}() => Make();")
				.And.Contains("public static void Hook_OnActivated_IKettle_")
				.Because("the public overloads produce or take another type, so they do not serve the registration");
		}

		[Test]
		public async Task EveryContainer_BuildsWithoutDiagnostics()
		{
			await That(Generator.Run(Library()).Diagnostics).IsEmpty();
			await That(Generator.Run(Library(grant: true)).Diagnostics).IsEmpty();
			await That(Generator.Run(Library() + Container).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer).Diagnostics).IsEmpty()
				.Because("a container in another assembly calls the wrappers of the internal factory and hook");
			await That(Generator.RunWithGeneratedReferencedAssembly(Library(), Consumer).Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("Internals.Factory_Make_").And.Contains("Internals.Hook_OnActivated_IKettle_");
			await That(Generator.RunWithGeneratedReferencedAssembly(Library(grant: true), Consumer).Diagnostics).IsEmpty();
		}
	}

	public class PrivateCompositeOfAnInternalService
	{
		private const string Container = """
			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class MyContainer;
			""";

		private static readonly string Composite = $"Composite_AllStores_{Fnv1a("global::Lib.CoffeeModule.AllStores\0global::Lib.IBeanStore"):x8}";

		private static string Library(bool consumed, bool grant = false) => $$"""
			using System.Collections.Generic;
			using Awaiten;

			{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

			namespace Lib;

			public interface IGrinder { }
			internal interface IBeanStore { }
			internal sealed class BeanStore : IBeanStore { }
			internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

			[Module]
			[Singleton<BeanStore, IBeanStore>]
			{{(consumed ? "[Singleton<Grinder, IGrinder>]" : "")}}
			[Composite<CoffeeModule.AllStores, IBeanStore>]
			public static partial class CoffeeModule
			{
			    private sealed class AllStores : IBeanStore { public AllStores(IEnumerable<IBeanStore> stores) { } }
			}
			""";

		[Test]
		[Arguments(true, false)]
		[Arguments(false, false)]
		[Arguments(true, true)]
		[Arguments(false, true)]
		public async Task Library_BuildsCleanly(bool consumed, bool grant)
		{
			GeneratorResult result = Generator.Run(Library(consumed, grant));

			await That(result.Diagnostics).IsEmpty()
				.Because("no public generated member names the internal service or the private composite");
			await That(ModuleSource(result)).Contains($"public static {Handle("global::Lib.IBeanStore")} {Composite}(")
				.And.Contains($"internal static global::Lib.IBeanStore Internal{Composite}(global::System.Collections.Generic.IEnumerable<global::Lib.IBeanStore> @stores)")
				.Because("the public factory builds the composite into the handle of the internal service, the internal one takes and returns the service itself");
		}

		[Test]
		public async Task ContainerInTheModulesAssembly_BuildsItThroughTheInternalFactory()
		{
			GeneratorResult result = Generator.Run(Library(consumed: true) + Container);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.Lib.MyContainer.g.cs"]).Contains($"global::Lib.CoffeeModule.Internals.Internal{Composite}(");
		}

		[Test]
		public async Task ContainerInAnAssemblyGrantedInternalsVisibleTo_BuildsItThroughTheInternalFactory()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(consumed: true, grant: true), "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container);
			string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

			await That(result.Diagnostics).IsEmpty();
			await That(source).Contains($"Internals.Internal{Composite}(").And.DoesNotContain("Hidden")
				.Because("a friend builds the internal services itself and registers no handles");
		}

		[Test]
		public async Task ContainerInAnotherAssembly_FrontsTheHandleWithIt()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(consumed: true), "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains($"Internals.{Composite}(")
				.Because("a container that cannot name the internal service builds the composite into its handle");
		}
	}

	public class PrivateDecoratorTakingAnInternalService
	{
		private const string OfTheInternalService = """
			[Decorate<CoffeeModule.LoggingStore, IBeanStore>]
			public static partial class CoffeeModule
			{
			    private sealed class LoggingStore : IBeanStore { public LoggingStore(IBeanStore inner) { } }
			}
			""";

		private const string OfAPublicService = """
			[Decorate<CoffeeModule.LoggingGrinder, IGrinder>]
			public static partial class CoffeeModule
			{
			    private sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner, IBeanStore store) { } }
			}
			""";

		private const string CompositeOfAPublicService = """
			[Composite<CoffeeModule.AllGrinders, IGrinder>]
			public static partial class CoffeeModule
			{
			    private sealed class AllGrinders : IGrinder { public AllGrinders(System.Collections.Generic.IEnumerable<IGrinder> all, IBeanStore store) { } }
			}
			""";

		private const string Container = """
			[Container]
			[Import(typeof(CoffeeModule))]
			public static partial class MyContainer;
			""";

		private static string Library(string decorator, bool grant = false) => $$"""
			using Awaiten;

			{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

			namespace Lib;

			public interface IGrinder { }
			internal interface IBeanStore { }
			internal sealed class BeanStore : IBeanStore { }
			internal sealed class Grinder : IGrinder { public Grinder(IBeanStore store) { } }

			[Module]
			[Singleton<BeanStore, IBeanStore>]
			[Singleton<Grinder, IGrinder>]
			{{decorator}}
			""";

		private static string Decorator(string decorator)
			=> decorator switch
			{
				OfTheInternalService => "Decorator_LoggingStore_",
				OfAPublicService => "Decorator_LoggingGrinder_",
				_ => "Composite_AllGrinders_",
			};

		[Test]
		[Arguments(OfTheInternalService)]
		[Arguments(OfAPublicService)]
		[Arguments(CompositeOfAPublicService)]
		public async Task Library_BuildsCleanly(string decorator)
		{
			GeneratorResult result = Generator.Run(Library(decorator));

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		[Arguments(OfTheInternalService)]
		[Arguments(OfAPublicService)]
		[Arguments(CompositeOfAPublicService)]
		public async Task ContainerInTheModulesAssembly_BuildsItThroughAnInternalFactory(string decorator)
		{
			GeneratorResult result = Generator.Run(Library(decorator) + Container);

			await That(result.Diagnostics).IsEmpty()
				.Because("a container of the module's own assembly builds the internal services itself and registers no handle");
			await That(result.Sources["Awaiten.Lib.MyContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Internals.Internal" + Decorator(decorator))
				.Because("it calls the factory that takes the internal services directly");
			await That(ModuleSource(result)).Contains($"internal static global::Lib.{(decorator == OfTheInternalService ? "IBeanStore" : "IGrinder")} Internal{Decorator(decorator)}")
				.Because("the module emits the internal factory beside the one that takes handles");
		}

		[Test]
		[Arguments(OfTheInternalService)]
		[Arguments(OfAPublicService)]
		[Arguments(CompositeOfAPublicService)]
		public async Task ContainerInAnAssemblyGrantedInternalsVisibleTo_BuildsItThroughTheInternalFactory(string decorator)
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(decorator, grant: true), "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container);

			await That(result.Diagnostics).IsEmpty()
				.Because("a friend constructs the internal services itself and registers no handle");
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("Internals.Internal" + Decorator(decorator));
		}

		[Test]
		[Arguments(OfTheInternalService)]
		[Arguments(OfAPublicService)]
		[Arguments(CompositeOfAPublicService)]
		public async Task ContainerInAnotherAssembly_TakesItThroughTheHandles(string decorator)
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(Library(decorator), "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container);
			string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

			await That(result.Diagnostics).IsEmpty();
			await That(source).Contains("Internals." + Decorator(decorator)).And.DoesNotContain("Internals.Internal" + Decorator(decorator))
				.Because("the internal factory is not accessible to an assembly without InternalsVisibleTo");
		}
	}

	public class Conflicts
	{
		private static string Library(string registrations) => $$"""
			using Awaiten;

			namespace Lib;

			public interface IPublic { }
			public interface IUser { }
			internal interface IInternal { }
			internal sealed class Impl : IInternal, IPublic { }
			internal sealed class User : IUser { public User(IInternal value) { } }

			[Module]
			{{registrations}}
			[Singleton<User, IUser>]
			public static partial class CoffeeModule
			{
			    internal static Impl Create() => new();
			    internal static void Activate(Impl impl) { }
			}
			""";

		[Test]
		public async Task LifetimeConflict_ReportsAwt107InTheLibrary()
		{
			GeneratorResult result = RunLibrary(Library("[Singleton<Impl, IInternal>]\n[Transient<Impl, IPublic>]"));

			await That(result.Diagnostics).Contains("*AWT107*'Lib.Impl'*Singleton*Transient*").AsWildcard()
				.Because("a container in the module's own assembly reports the conflict, while one in another assembly serves every service from one instance handle");
		}

		[Test]
		public async Task ProductionConflict_ReportsAwt111InTheLibrary()
		{
			GeneratorResult result = RunLibrary(Library("[Singleton<Impl, IInternal>]\n[Singleton<Impl, IPublic>(Factory = nameof(Create))]"));

			await That(result.Diagnostics).Contains("*AWT111*'Lib.Impl'*a constructor*factory 'Lib.CoffeeModule.Create'*").AsWildcard()
				.Because("the handle builds the implementation one way, so a second production would silently go its own way in another assembly");
		}

		[Test]
		[Arguments("[Singleton<Impl, IInternal>]\n[Singleton<Impl, IPublic>(SuppressDisposal = true)]", "SuppressDisposal")]
		[Arguments("[Singleton<Impl, IInternal>]\n[Singleton<Impl, IPublic>(Eager = true)]", "Eager")]
		[Arguments("[Singleton<Impl, IInternal>]\n[Singleton<Impl, IPublic>(OnActivated = nameof(Activate))]", "OnActivated")]
		public async Task DirectiveOnALaterRegistration_ReportsAwt166InTheLibrary(string registrations, string directive)
		{
			GeneratorResult result = RunLibrary(Library(registrations));

			await That(result.Diagnostics).Contains($"*AWT166*'Lib.Impl'*{directive}*").AsWildcard()
				.Because("the instance handle takes the directives of the first registration, as a container in the module's own assembly does, which reports the later one");
		}

		[Test]
		public async Task ConflictWithAContainerInTheLibrary_IsReportedOnce()
		{
			GeneratorResult result = RunLibrary(Library("[Singleton<Impl, IInternal>]\n[Transient<Impl, IPublic>]") + """

				[Container]
				[Import(typeof(CoffeeModule))]
				public static partial class LibContainer;
				""");

			await That(result.Diagnostics.Count(diagnostic => diagnostic.Contains("AWT107"))).IsEqualTo(1)
				.Because("the container of the library reports the conflict at the same place, so the module's build leaves it to the container");
		}

		[Test]
		public async Task ConsistentRegistrations_DoNotReportAConflict()
		{
			GeneratorResult result = RunLibrary(Library("[Singleton<Impl, IInternal>(SuppressDisposal = true, OnActivated = nameof(Activate))]\n[Singleton<Impl, IPublic>]"));

			await That(result.Diagnostics).IsEmpty()
				.Because("a later registration that leaves a directive unset merges with the first one");
		}

		[Test]
		public async Task LifetimeConflictAcrossModules_ReportsAwt107InTheConsumer()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IFront { }
				public interface IBack { }
				internal interface IInternal { }
				internal sealed class Impl : IInternal, IFront, IBack { }

				[Module]
				[Singleton<Impl, IInternal>]
				[Singleton<Impl, IFront>]
				public static partial class CoffeeModule { }

				[Module]
				[Transient<Impl, IBack>]
				public static partial class RoastingModule { }
				""", """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(CoffeeModule))]
				[Import(typeof(RoastingModule))]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).Contains("*AWT107*'Lib.Impl'*Singleton*Transient*").AsWildcard()
				.Because("the modules build the implementation into one instance handle, whose registrations the container checks like any other");
		}

		public class OverridableDefaults
		{
			private const string Library = """
				using Awaiten;

				namespace Lib;

				public interface IClock { }
				internal interface ITick { }
				internal sealed class Clock : IClock, ITick { }

				[Module]
				[Singleton<Clock, IClock>(Eager = true, Fallback = Fallback.Silent)]
				[Singleton<Clock, ITick>]
				public static partial class ClockModule { }
				""";

			private const string Container = """
				[Container]
				[Import(typeof(ClockModule))]
				public static partial class MyContainer;
				""";

			[Test]
			public async Task DirectiveOfASurvivingDefault_ReportsAwt166LikeTheModulesOwnAssembly()
			{
				GeneratorResult same = Generator.Run(Library + Container);
				GeneratorResult cross = Generator.RunWithGeneratedReferencedAssembly(Library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container);

				await That(same.Diagnostics).Contains("*AWT166*'Lib.Clock'*Eager*").AsWildcard();
				await That(cross.Diagnostics).Contains("*AWT166*'Lib.Clock'*Eager*").AsWildcard()
					.Because("the strong registration comes first and keeps the clock lazy, so the surviving eager default contradicts it, as in the module's own assembly");
			}

			[Test]
			public async Task DirectiveOfAnOverriddenDefault_IsDropped()
			{
				GeneratorResult cross = Generator.RunWithGeneratedReferencedAssembly(Library, """
					using Awaiten;
					using Lib;

					namespace MyCode;

					public sealed class MyClock : IClock { }

					[Container]
					[Import(typeof(ClockModule))]
					[Singleton<MyClock, IClock>]
					public static partial class MyContainer;
					""");

				await That(cross.Diagnostics).IsEmpty()
					.Because("the container overrides the eager default, which then contributes nothing, as in the module's own assembly");
			}

			[Test]
			public async Task ContextualDefaultBesideAnOverrideOfItsService_KeepsItsDirectives()
			{
				const string library = """
					using Awaiten;

					namespace Lib;

					public interface IClock { }
					public sealed class Watch { public Watch(IClock clock) { } }
					internal interface ITick { }
					internal sealed class Clock : IClock, ITick { }

					[Module]
					[Singleton<Clock, IClock>(Fallback = Fallback.Silent, WhenInjectedInto = typeof(Watch))]
					[Singleton<Clock, ITick>(Fallback = Fallback.Silent, Eager = true)]
					[Singleton<Watch>]
					public static partial class ClockModule { }
					""";
				const string container = """
					public sealed class MyClock : IClock { }

					[Container]
					[Import(typeof(ClockModule))]
					[Singleton<MyClock, IClock>]
					public static partial class MyContainer;
					""";
				GeneratorResult same = Generator.Run(library + container);
				GeneratorResult cross = Generator.RunWithGeneratedReferencedAssembly(library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + container);

				await That(same.Diagnostics).Contains("*AWT166*'Lib.Clock'*Eager*").AsWildcard();
				await That(cross.Diagnostics).Contains("*AWT166*'Lib.Clock'*Eager*").AsWildcard()
					.Because("the override claims the unkeyed service only, so the contextual default survives and comes first, as in the module's own assembly");
			}
		}

		public class EveryRegistrationOverridden
		{
			private static string Library(string conventions = "") => $$"""
				using Awaiten;
				using System.Threading.Tasks;

				namespace Lib;

				public interface IA { }
				public interface IB { }
				public sealed class Dep { }
				internal interface IStartable { Task StartAsync(); }
				internal sealed class Impl : IA, IB, IStartable { public Impl(Dep dep) { } public Task StartAsync() => Task.CompletedTask; }

				[Module]
				{{conventions}}
				[Singleton<Impl, IA>(Fallback = Fallback.Silent)]
				[Singleton<Impl, IB>(Fallback = Fallback.Silent)]
				public static partial class AModule { }
				""";

			private const string Container = """
				public sealed class MyA : IA { }
				public sealed class MyB : IB { }

				[Container]
				[Import(typeof(AModule))]
				[Singleton<MyA, IA>]
				[Singleton<MyB, IB>]
				public static partial class MyContainer;
				""";

			[Test]
			public async Task DependencyOfTheImplementation_IsNotRequired()
			{
				GeneratorResult same = Generator.Run(Library() + Container);
				GeneratorResult cross = Generator.RunWithGeneratedReferencedAssembly(Library(), "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container);

				await That(same.Diagnostics).IsEmpty();
				await That(cross.Diagnostics).IsEmpty()
					.Because("the container overrides every registration of the implementation, so, as in the module's own assembly, nothing builds it");
			}

			[Test]
			public async Task AsyncInitializedImplementation_IsNotBuiltAtStartup()
			{
				string library = Library("[AsyncInitializer<IStartable>(nameof(IStartable.StartAsync))]");
				GeneratorResult cross = Generator.RunWithGeneratedReferencedAssembly(library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\npublic sealed class MyDep { }\n" + Container.Replace("[Singleton<MyA, IA>]", "[Singleton<MyA, IA>]\n[Singleton<Dep>]"));

				await That(cross.Diagnostics).IsEmpty();
				await That(cross.Sources["Awaiten.MyCode.MyContainer.g.cs"]).DoesNotContain("HiddenRegistration_Impl")
					.Because("the handle of an implementation whose every registration is overridden is dropped with its initializer");
			}
		}

		public class OverridableDefaultsOfAModule
		{
			private const string Library = """
				using Awaiten;

				namespace Lib;

				public interface IA { }
				public interface IB { }
				internal interface IStore { }
				internal sealed class Store : IA, IB, IStore { }

				[Module]
				[Transient<Store, IA>(Fallback = Fallback.Silent)]
				[Transient<Store, IB>(Fallback = Fallback.Silent)]
				public static partial class AModule { }

				[Module]
				[Singleton<Store, IStore>]
				public static partial class BModule { }
				""";

			private static string Container(params string[] modules) => $$"""
				[Container]
				{{string.Join("\n", modules.Select(module => $"[Import(typeof({module}))]"))}}
				public static partial class MyContainer;
				""";

			[Test]
			[Arguments("AModule", "BModule")]
			[Arguments("BModule", "AModule")]
			public async Task LifetimeConflictWithAnotherModule_ReportsAwt107LikeTheModulesOwnAssembly(string first, string second)
			{
				GeneratorResult same = Generator.Run(Library + Container(first, second));
				GeneratorResult cross = Generator.RunWithGeneratedReferencedAssembly(Library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container(first, second));

				await That(same.Diagnostics).Contains("*AWT107*'Lib.Store'*Singleton*Transient*").AsWildcard();
				await That(cross.Diagnostics).Contains("*AWT107*'Lib.Store'*Singleton*Transient*").AsWildcard()
					.Because("nothing contests the services of the overridable defaults, so they survive and contradict the singleton of the other module, as in the module's own assembly");
			}

			[Test]
			public async Task LifetimeConflictWithinTheModule_ReportsAwt107LikeTheModulesOwnAssembly()
			{
				const string library = """
					using Awaiten;

					namespace Lib;

					public interface IA { }
					internal interface IStore { }
					internal sealed class Store : IA, IStore { }

					[Module]
					[Transient<Store, IA>(Fallback = Fallback.Silent)]
					[Singleton<Store, IStore>]
					public static partial class AModule { }
					""";
				GeneratorResult same = Generator.Run(library + Container("AModule"));
				GeneratorResult cross = Generator.RunWithGeneratedReferencedAssembly(library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container("AModule"));

				await That(Generator.Run(library).Diagnostics).IsEmpty()
					.Because("only the importing container knows whether the overridable default survives");
				await That(same.Diagnostics).Contains("*AWT107*'Lib.Store'*Singleton*Transient*").AsWildcard();
				await That(cross.Diagnostics).Contains("*AWT107*'Lib.Store'*Singleton*Transient*").AsWildcard()
					.Because("the surviving default contradicts the lifetime of the strong registration, which the handle takes, as in the module's own assembly");
			}
		}

		private const string DirectiveModules = """
			using Awaiten;

			namespace Lib;

			public interface IGrinder { }
			internal sealed class Store : System.IDisposable { public void Dispose() { } }
			internal sealed class Grinder : IGrinder { public Grinder(Store store) { } }

			[Module]
			[Singleton<Store>]
			public static partial class AModule { }

			[Module]
			[Singleton<Store>(OnActivated = nameof(Activate), SuppressDisposal = true)]
			[Singleton<Grinder, IGrinder>]
			public static partial class BModule
			{
			    internal static void Activate(Store store) { }
			}
			""";

		[Test]
		public async Task DirectivesOfALaterImport_ReportAwt166InTheConsumer()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(DirectiveModules, """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(AModule))]
				[Import(typeof(BModule))]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).Contains("*AWT166*'Lib.Store'*OnActivated*").AsWildcard()
				.And.Contains("*AWT166*'Lib.Store'*SuppressDisposal*").AsWildcard()
				.Because("the first import wins and the later one sets directives it does not, as in a container of the modules' own assembly");
			await That(result.Diagnostics).Contains("*AWT166*'Lib.BModule.Activate'*").AsWildcard()
				.And.DoesNotContain("*Hook_*").AsWildcard()
				.Because("the message names the hook the module declares, not its generated wrapper");
		}

		[Test]
		public async Task HookOfASelfCompiledRegistrationOfALaterImport_IsNamedInAwt166()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly("""
				using Awaiten;

				namespace Lib;

				public interface IFront { }
				public interface IBack { }
				internal sealed class Impl : IFront, IBack { }

				[Module]
				[Singleton<Impl, IFront>]
				public static partial class AModule { }

				[Module]
				[Singleton<Impl, IBack>(OnActivated = nameof(Activate))]
				public static partial class BModule
				{
				    internal static void Activate(Impl impl) { }
				}
				""", """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(AModule))]
				[Import(typeof(BModule))]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).Contains("*AWT166*'Lib.Impl'*OnActivated*'Lib.BModule.Activate'*").AsWildcard()
				.And.DoesNotContain("*Hook_*").AsWildcard()
				.Because("the message names the hook the module declares, not its generated wrapper");
		}

		[Test]
		public async Task ProductionConflictAcrossModules_ReportsAwt111InTheConsumer()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(DirectiveModules.Replace("[Singleton<Store>(OnActivated = nameof(Activate), SuppressDisposal = true)]", "[Singleton<Store>(Factory = nameof(Create))]")
				.Replace("internal static void Activate(Store store) { }", "internal static Store Create() => new();"), """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(AModule))]
				[Import(typeof(BModule))]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).Contains("*AWT111*'Lib.Store'*a constructor*factory 'Lib.BModule.Create'*").AsWildcard()
				.Because("the modules produce the implementation differently, which their generated factories hide");
		}

		[Test]
		public async Task DirectivesOfTheFirstImport_AreKeptWithoutAConflict()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(DirectiveModules, """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(BModule))]
				[Import(typeof(AModule))]
				public static partial class MyContainer;
				""");
			string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

			await That(result.Diagnostics).IsEmpty()
				.Because("a later registration that leaves the directives unset merges with the first one");
			await That(source).Contains("global::Lib.BModule.Internals.HiddenHook_OnActivated_Store_")
				.Because("the hook of the first import runs through its own wrapper");
		}
	}

	public class Diagnostics
	{
		[Test]
		public async Task InternalServiceThatCannotBeBuilt_ReportsItsCause()
		{
			GeneratorResult result = RunLibrary("""
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class Silo { }
				internal sealed class BeanStore { public BeanStore(Silo silo) { } }
				internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

				[Module]
				[Singleton<BeanStore>]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }
				""");

			await That(result.Diagnostics).Contains("*AWT195*'Lib.BeanStore' is registered under the internal service*'Lib.Silo'*register it in the module*").AsWildcard()
				.Because("the internal service needs an internal type the module does not register");
		}

		[Test]
		public async Task InternalServiceThatCannotBeBuilt_IsReportedOnceAndNamedByAContainerInAnotherAssembly()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class Silo { }
				internal sealed class BeanStore { public BeanStore(Silo silo) { } }
				internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

				[Module]
				[Singleton<BeanStore>]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }
				""";

			GeneratorResult result = Generator.Run(library);
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n[Container]\n[Import(typeof(CoffeeModule))]\npublic static partial class MyContainer;");

			await That(result.Diagnostics).HasSingle()
				.Because("the consumer of the internal service fails only because the service cannot be built, which the module reports once");
			await That(result.Diagnostics).Contains("*AWT195*'Lib.BeanStore' is registered under the internal service*'Lib.Silo'*").AsWildcard();
			await That(external.Diagnostics).DoesNotContain("*AWT154*").AsWildcard()
				.Because("the module's assembly was built with the generator, so blaming a missing generator would mislead");
			await That(external.Diagnostics).Contains("*AWT231*'Lib.Grinder'*because its constructor parameter 'store' needs the internal service 'Lib.BeanStore', which the module cannot build*").AsWildcard();
		}

		[Test]
		public async Task InternalServiceThatCannotBeBuilt_TakenThroughAnInjectedProperty_IsReportedOnceAndNamedByAContainerInAnotherAssembly()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class Silo { }
				internal sealed class BeanStore { public BeanStore(Silo silo) { } }
				internal sealed class Grinder : IGrinder { [Inject] public BeanStore Store { get; set; } = null!; }

				[Module]
				[Singleton<BeanStore>]
				[Transient<Grinder, IGrinder>]
				public static partial class CoffeeModule { }
				""";

			GeneratorResult result = Generator.Run(library);
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n[Container]\n[Import(typeof(CoffeeModule))]\npublic static partial class MyContainer;");

			await That(result.Diagnostics).HasSingle()
				.Because("the property fails only because the service cannot be built, which the module reports once");
			await That(result.Diagnostics).Contains("*AWT195*'Lib.BeanStore' is registered under the internal service*'Lib.Silo'*").AsWildcard();
			await That(external.Diagnostics).DoesNotContain("*AWT154*").AsWildcard()
				.Because("the module's assembly was built with the generator, so blaming a missing generator would mislead");
			await That(external.Diagnostics).Contains("*AWT231*'Lib.Grinder'*because its property 'Store' needs the internal service 'Lib.BeanStore', which the module cannot build*").AsWildcard();
		}

		[Test]
		public async Task InternalServiceThatCannotBeBuilt_TakenByAScanMatch_IsReportedOnceAndNamedByAContainerInAnotherAssembly()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class Silo { }
				internal sealed class BeanStore { public BeanStore(Silo silo) { } }
				internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

				[Module]
				[Singleton<BeanStore>]
				[Scan(typeof(IGrinder), As = ScanAs.Marker)]
				public static partial class CoffeeModule { }
				""";

			GeneratorResult result = Generator.Run(library);
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n[Container]\n[Import(typeof(CoffeeModule))]\npublic static partial class MyContainer;");

			await That(result.Diagnostics).HasSingle()
				.Because("the scan match fails only because the service cannot be built, which the module reports once");
			await That(result.Diagnostics).Contains("*AWT195*'Lib.BeanStore' is registered under the internal service*'Lib.Silo'*").AsWildcard();
			await That(external.Diagnostics).Contains("*AWT231*'Lib.Grinder'*because its constructor parameter 'store' needs the internal service 'Lib.BeanStore', which the module cannot build*").AsWildcard()
				.Because("a container in another assembly would otherwise lose the match without a word");
		}

		[Test]
		public async Task InternalServiceThatCannotBeBuiltThroughItsFactory_IsNamedOnceByAContainerInAnotherAssembly()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class Silo { }
				internal sealed class BeanStore { }
				internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

				[Module]
				[Singleton<BeanStore>(Factory = nameof(Make))]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule
				{
				    internal static BeanStore Make(Silo silo) => new();
				}
				""";

			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n[Container]\n[Import(typeof(CoffeeModule))]\npublic static partial class MyContainer;");

			await That(external.Diagnostics).HasSingle()
				.Because("the container can neither name the internal service nor call its factory, so the registration is out of its reach like a constructed one");
			await That(external.Diagnostics).Contains("*AWT231*'Lib.Grinder'*because its constructor parameter 'store' needs the internal service 'Lib.BeanStore', which the module cannot build*").AsWildcard();
		}

		[Test]
		public async Task EagerInternalSingletonThatCannotBeBuiltThroughItsFactory_IsReportedByAContainerInAnotherAssembly()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				internal sealed class Silo { }
				internal sealed class BeanStore { }

				[Module]
				[Singleton<BeanStore>(Factory = nameof(Make), Eager = true)]
				public static partial class CoffeeModule
				{
				    internal static BeanStore Make(Silo silo) => new();
				}
				""";

			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n[Container]\n[Import(typeof(CoffeeModule))]\npublic static partial class MyContainer;");

			await That(external.Diagnostics).HasSingle()
				.Because("a container starts an eager singleton though nothing depends on it, so leaving it out must not go unnoticed");
			await That(external.Diagnostics).Contains("*AWT231*'Lib.BeanStore'*generated no handle for it*").AsWildcard();
		}

		[Test]
		public async Task ChainOfInternalServicesThatCannotBeBuilt_ReportsOnlyTheCauseAtItsEnd()
		{
			const string library = """
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class Silo { }
				internal sealed class Hopper { public Hopper(Silo silo) { } }
				internal sealed class BeanStore { public BeanStore(Hopper hopper) { } }
				internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

				[Module]
				[Singleton<BeanStore>]
				[Singleton<Hopper>]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }
				""";

			GeneratorResult result = Generator.Run(library);
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n[Container]\n[Import(typeof(CoffeeModule))]\npublic static partial class MyContainer;");

			await That(result.Diagnostics).HasSingle()
				.Because("BeanStore fails only because Hopper cannot be built, so only Hopper's own cause is worth fixing");
			await That(result.Diagnostics).Contains("*AWT195*'Lib.Hopper' is registered under the internal service*'silo' has the type 'Lib.Silo'*").AsWildcard();
			await That(external.Diagnostics).Contains("*AWT231*'Lib.Grinder'*because its constructor parameter 'store' needs the internal service 'Lib.BeanStore', which the module cannot build*").AsWildcard();
		}

		[Test]
		public async Task InternalServiceThatCannotBeBuilt_TakenAsArg_ReportsTheParameterOfTheConsumer()
		{
			GeneratorResult result = RunLibrary("""
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class Silo { }
				internal sealed class BeanStore { public BeanStore(Silo silo) { } }
				internal sealed class Grinder : IGrinder { public Grinder([Arg] BeanStore store) { } }

				[Module]
				[Singleton<BeanStore>]
				[Transient<Grinder, IGrinder>]
				public static partial class CoffeeModule { }
				""");

			await That(result.Diagnostics).Contains("*AWT195*'Lib.Grinder'*its constructor parameter 'store' has the type 'Lib.BeanStore'*").AsWildcard()
				.Because("a caller passes an [Arg] to the generated factory, which therefore needs a type the caller can name, whether or not the module can build the service");
		}

		[Test]
		public async Task InternalServiceThatCannotBeBuilt_TakenAsRequestingType_ReportsTheParameterOfTheConsumer()
		{
			GeneratorResult result = RunLibrary("""
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal sealed class Silo { }
				internal sealed class BeanStore { public BeanStore(Silo silo) { } }
				internal sealed class Hopper { }
				internal sealed class Grinder : IGrinder { public Grinder(Hopper hopper) { } }

				[Module]
				[Singleton<BeanStore>]
				[Transient<Hopper>(Factory = nameof(MakeHopper))]
				[Transient<Grinder, IGrinder>]
				public static partial class CoffeeModule
				{
				    internal static Hopper MakeHopper([RequestingType] BeanStore store) => new();
				}
				""");

			await That(result.Diagnostics).Contains("*AWT195*'Lib.Hopper'*the parameter 'store' of its factory 'MakeHopper' has the type 'Lib.BeanStore'*").AsWildcard()
				.Because("a [RequestingType] parameter is filled by the consumer and never resolved from the module's handles");
		}

		[Test]
		public async Task InternalServiceThatCannotBeBuilt_WithInternalsVisibleTo_IsLeftToTheFriendAssembly()
		{
			GeneratorResult result = RunLibrary("""
				using Awaiten;

				[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Friend")]

				namespace Lib;

				public interface IGrinder { }
				internal sealed class Silo { }
				internal sealed class BeanStore { public BeanStore(Silo silo) { } }
				internal sealed class Grinder : IGrinder { public Grinder(BeanStore store) { } }

				[Module]
				[Singleton<BeanStore>]
				[Singleton<Grinder, IGrinder>]
				public static partial class CoffeeModule { }
				""");

			await That(result.Diagnostics).IsEmpty()
				.Because("an assembly granted InternalsVisibleTo constructs both itself, as before");
		}

		public class SynchronousShapeOfAnAsyncInitializedInternalService
		{
			private static string Library(string shape, bool grant = false) => $$"""
				using System.Threading.Tasks;
				using Awaiten;

				{{(grant ? "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]" : "")}}

				namespace Lib;

				public interface IReport { }
				internal interface IStore { Task StartAsync(); }
				internal sealed class Store : IStore { public Task StartAsync() => Task.CompletedTask; }
				internal sealed class Report : IReport { public Report({{shape}} store) { } }

				[Module]
				[AsyncInitializer<IStore>(nameof(IStore.StartAsync))]
				[Singleton<Store, IStore>]
				[Transient<Report, IReport>]
				public static partial class CoffeeModule { }
				""";

			private const string Container = """
				[Container]
				[Import(typeof(CoffeeModule))]
				public static partial class MyContainer;
				""";

			private const string Consumer = "using Awaiten;\nusing Lib;\n\nnamespace MyCode;\n\n" + Container;

			[Test]
			[Arguments("System.Func<IStore>", "Func")]
			[Arguments("System.Lazy<IStore>", "Lazy")]
			[Arguments("Owned<IStore>", "Owned")]
			[Arguments("System.Func<Owned<IStore>>", "Func")]
			public async Task EveryContainer_ReportsAwt119(string shape, string relationship)
			{
				string expected = $"*AWT119*'Lib.Report' resolves 'Lib.Store' through a synchronous {relationship}<>*";

				await That(Generator.Run(Library(shape) + Container).Diagnostics).Contains(expected).AsWildcard();
				await That(Generator.RunWithGeneratedReferencedAssembly(Library(shape, grant: true), Consumer).Diagnostics).Contains(expected).AsWildcard();
				await That(Generator.RunWithGeneratedReferencedAssembly(Library(shape), Consumer).Diagnostics).Contains(expected).AsWildcard()
					.And.DoesNotContain("*AWT120*").AsWildcard()
					.Because("the relationship reaches the async-initialized store through its handle, which a container in another assembly judges like the store itself");
			}

			[Test]
			public async Task ACollection_ReportsAwt122InEveryContainer()
			{
				const string shape = "System.Collections.Generic.IEnumerable<IStore>";

				const string expected = "*AWT122*'Lib.Report' resolves the collection of*the member 'Lib.Store' is async-tainted*";

				await That(Generator.Run(Library(shape) + Container).Diagnostics).Contains(expected).AsWildcard();
				await That(Generator.RunWithGeneratedReferencedAssembly(Library(shape, grant: true), Consumer).Diagnostics).Contains(expected).AsWildcard();
				await That(Generator.RunWithGeneratedReferencedAssembly(Library(shape), Consumer).Diagnostics).Contains(expected).AsWildcard()
					.Because("a container in another assembly names the async-initialized member behind the handle, as a container in the module's assembly does");
			}
		}

		public class RegisteredByAnotherModuleToo
		{
			private static string Library(string aModule, string cModule) => $$"""
				using Awaiten;

				namespace Lib;

				public interface IGrinder { }
				internal interface IStore { }
				internal sealed class Options { }
				internal sealed class BStore : IStore { public BStore(Options options) { } }
				internal sealed class CStore : IStore { }
				internal sealed class Grinder : IGrinder { public Grinder(IStore store) { } }

				[Module]
				{{aModule}}
				public static partial class AModule { }

				[Module]
				[Singleton<BStore, IStore>]
				public static partial class BModule { }

				[Module]
				{{cModule}}
				public static partial class CModule { }
				""";

			[Test]
			public async Task ImplementationThatCannotBeBuilt_ReportsItsCause()
			{
				string library = Library("[Singleton<Grinder, IGrinder>]", "[Singleton<CStore, IStore>]");
				GeneratorResult same = Generator.Run(library + """

					[Container]
					[Import(typeof(AModule))]
					[Import(typeof(BModule))]
					[Import(typeof(CModule))]
					public static partial class MyContainer;
					""");

				await That(same.Diagnostics).Contains("*AWT101*'Lib.BStore' requires 'Lib.Options'*").AsWildcard();
				await That(Generator.Run(library).Diagnostics).Contains("*AWT195*'Lib.BStore' is registered under the internal service 'Lib.IStore'*'Lib.Options'*").AsWildcard()
					.Because("a container in another assembly would otherwise drop the registration silently and serve the store of the other module, which a container in the module's own assembly does not");
			}

			[Test]
			public async Task ImplementationThatCannotBeBuiltBesideOneAnotherModuleNeeds_ReportsItsCause()
			{
				GeneratorResult library = Generator.Run(Library("", "[Singleton<CStore, IStore>]\n[Singleton<Grinder, IGrinder>]"));

				await That(library.Diagnostics).Contains("*AWT195*'Lib.BStore'*'Lib.Options'*").AsWildcard()
					.Because("an import of this module before the other one makes its broken registration win in the module's own assembly");
			}

			[Test]
			public async Task ImplementationThatCannotBeBuiltUnderAServiceNoOtherModuleRegisters_ReportsNothing()
			{
				GeneratorResult library = Generator.Run(Library("", ""));

				await That(library.Diagnostics).IsEmpty()
					.Because("no other module takes or registers the internal service, so a container in another assembly cannot reach the registration");
			}
		}
	}
}
