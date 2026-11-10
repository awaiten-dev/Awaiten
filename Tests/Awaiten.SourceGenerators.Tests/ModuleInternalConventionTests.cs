namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     An <c>[AsyncInitializer&lt;T&gt;]</c> convention of a public module whose type or method is internal: a container
///     in another assembly runs it through a wrapper the module generates, or through the wrapper of a generated handle,
///     while a container in the module's assembly or in a friend calls it directly. A convention no container can run is
///     reported, never left out.
/// </summary>
public class ModuleInternalConventionTests
{
	private const string ExternalContainer = """
		using Awaiten;
		using Lib;

		namespace MyCode;

		[Container]
		[Import(typeof(CoffeeModule))]
		public static partial class MyContainer;
		""";

	private const string SameAssemblyContainer = """

		[Container]
		[Import(typeof(CoffeeModule))]
		public static partial class SameContainer;
		""";

	private const string MethodConvention = "[AsyncInitializer<Brew>(nameof(Brew.WarmUpAsync))]";

	private const string InterfaceConvention = "[AsyncInitializer<IWarm>(nameof(IWarm.WarmUpAsync))]";

	private static (GeneratorResult Library, GeneratorResult Same, GeneratorResult External, GeneratorResult Reference, GeneratorResult Compilation, GeneratorResult Friend) RunAll(string library, string external = ExternalContainer)
		=> (Generator.Run(library),
			Generator.Run(library + SameAssemblyContainer),
			Generator.RunWithGeneratedReferencedAssembly(library, external),
			Generator.RunWithGeneratedReferenceAssembly(library, external),
			Generator.RunWithGeneratedCompilationReference(library, external),
			Generator.RunWithGeneratedReferencedAssembly(library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;"), external));

	private static string Library(string types, string registrations, string module = "public static partial class CoffeeModule;") => $$"""
		using Awaiten;
		using System.Threading;
		using System.Threading.Tasks;

		namespace Lib;

		public interface IBrew { }
		public interface IDrink { }
		internal interface IWarm { Task WarmUpAsync(); }
		{{types}}

		[Module]
		{{registrations}}
		{{module}}
		""";

	private static string Brew(bool publicImplementation, bool interfaceConvention)
		=> $"{(publicImplementation ? "public" : "internal")} sealed class Brew : IBrew, IDrink{(interfaceConvention ? ", IWarm" : string.Empty)} {{ {(interfaceConvention ? "public" : "internal")} Task WarmUpAsync() => Task.CompletedTask; }}";

	private static async Task AssertRunsOnceEverywhere(string library)
	{
		(GeneratorResult built, GeneratorResult same, GeneratorResult other, GeneratorResult reference, GeneratorResult compilation, GeneratorResult friend) = RunAll(library);

		await That(built.Diagnostics).IsEmpty()
			.Because("the module generates a wrapper for every convention a container in another assembly cannot call");
		await That(same.Diagnostics).IsEmpty();
		await That(other.Diagnostics).IsEmpty();
		await That(reference.Diagnostics).IsEmpty();
		await That(compilation.Diagnostics).IsEmpty();
		await That(friend.Diagnostics).IsEmpty();
		foreach (GeneratorResult result in new[] { other, reference, compilation, })
		{
			string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
			await That(source).Contains("await global::Lib.CoffeeModule.Internals.").Exactly(1)
				.Because("a container in another assembly runs the convention once through the module's wrapper");
			await That(source).Contains("Initializer_");
			await That(source).DoesNotContain(").WarmUpAsync(");
		}

		await That(same.Sources["Awaiten.Lib.SameContainer.g.cs"]).Contains(").WarmUpAsync()").Exactly(1)
			.Because("a container in the module's assembly calls the convention directly");
		await That(friend.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains(").WarmUpAsync()").Exactly(1)
			.Because("a friend sees the internal convention and calls it directly");
	}

	public class Registrations
	{
		[Test]
		[Arguments(false, false, "[Singleton<Brew, IBrew>]")]
		[Arguments(false, true, "[Singleton<Brew, IBrew>]")]
		[Arguments(true, false, "[Singleton<Brew, IBrew>]")]
		[Arguments(true, true, "[Singleton<Brew, IBrew>]")]
		[Arguments(false, false, "[Singleton<Brew, IBrew>][Singleton<Brew, IDrink>]")]
		[Arguments(false, true, "[Singleton<Brew, IBrew>][Singleton<Brew, IDrink>]")]
		[Arguments(true, false, "[Singleton<Brew, IBrew>][Singleton<Brew, IDrink>]")]
		[Arguments(true, true, "[Singleton<Brew, IBrew>][Singleton<Brew, IDrink>]")]
		[Arguments(true, false, "[Transient<Brew>]")]
		[Arguments(false, false, "[Scan<IBrew>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]")]
		[Arguments(false, true, "[Scan<IBrew>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]")]
		[Arguments(true, false, "[Scan<IBrew>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]")]
		[Arguments(true, true, "[Scan<IBrew>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]")]
		[Arguments(false, false, "[Scan<IDrink>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]")]
		public async Task AnInternalConvention_RunsOnceInEveryContainer(bool publicImplementation, bool interfaceConvention, string registrations)
			=> await AssertRunsOnceEverywhere(Library(Brew(publicImplementation, interfaceConvention), (interfaceConvention ? InterfaceConvention : MethodConvention) + registrations));

		[Test]
		public async Task OfAFactoryMember_RunsOnceInEveryContainer()
			=> await AssertRunsOnceEverywhere(Library(
				Brew(publicImplementation: true, interfaceConvention: false),
				MethodConvention + "[Singleton<Brew, IBrew>(Factory = nameof(CoffeeModule.Create))]",
				"public static partial class CoffeeModule { public static Brew Create() => new(); }"));

		[Test]
		[Arguments(false, "private static Brew Make() => new();")]
		[Arguments(true, "private static Brew Make() => new();")]
		[Arguments(false, "internal static Brew Make() => new();")]
		[Arguments(true, "internal static Brew Make() => new();")]
		public async Task OfAFactoryMemberReturningAnInternalImplementation_RunsOnceInEveryContainer(bool interfaceConvention, string factory)
			=> await AssertRunsOnceEverywhere(Library(
				Brew(publicImplementation: false, interfaceConvention),
				(interfaceConvention ? InterfaceConvention : MethodConvention) + "[Singleton<IBrew>(Factory = nameof(Make))]",
				$"public static partial class CoffeeModule {{ {factory} }}"));

		[Test]
		[Arguments("[Singleton<Brew, IBrew>(Factory = nameof(Make))]")]
		[Arguments("[Singleton<IBrew>(Factory = nameof(Make))]")]
		public async Task OfAFactoryMemberReturningADerivedInternalImplementation_RunsOnceInEveryContainer(string registration)
			=> await AssertRunsOnceEverywhere(Library(
				"internal class Brew : IBrew { }\ninternal sealed class Special : Brew { internal Task WarmUpAsync() => Task.CompletedTask; }",
				"[AsyncInitializer<Special>(nameof(Special.WarmUpAsync))]" + registration,
				"public static partial class CoffeeModule { private static Special Make() => new(); }"));

		[Test]
		[Arguments(false, "private static Task<Brew> Make() => Task.FromResult(new Brew());")]
		[Arguments(true, "private static ValueTask<Brew> Make() => new(new Brew());")]
		public async Task OfAnAsyncFactoryMemberReturningAnInternalImplementation_RunsOnceInEveryContainer(bool interfaceConvention, string factory)
		{
			(GeneratorResult built, GeneratorResult same, GeneratorResult other, GeneratorResult reference, GeneratorResult compilation, GeneratorResult friend) = RunAll(Library(
				Brew(publicImplementation: false, interfaceConvention),
				(interfaceConvention ? InterfaceConvention : MethodConvention) + "[Singleton<IBrew>(Factory = nameof(Make))]",
				$"public static partial class CoffeeModule {{ {factory} }}"));

			await That(built.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			foreach (GeneratorResult result in new[] { other, reference, compilation, })
			{
				await That(result.Diagnostics).IsEmpty();
				await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("await global::Lib.CoffeeModule.Internals.Initializer_").Exactly(1)
					.Because("the module records the convention of the brew its wrapper awaits and returns as the public service");
			}

			await That(same.Sources["Awaiten.Lib.SameContainer.g.cs"]).Contains(").WarmUpAsync()").Exactly(1);
			await That(friend.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains(").WarmUpAsync()").Exactly(1);
		}

		[Test]
		public async Task OfAFactoryMemberReturningAnInternalImplementation_WithAPublicConvention_CallsItDirectly()
		{
			string library = Library(
				"public interface ICheck { Task CheckAsync(); }\ninternal sealed class Brew : IBrew, ICheck { public Task CheckAsync() => Task.CompletedTask; }",
				"[AsyncInitializer<ICheck>(nameof(ICheck.CheckAsync))][Singleton<IBrew>(Factory = nameof(Make))]",
				"public static partial class CoffeeModule { private static Brew Make() => new(); }");

			foreach (GeneratorResult result in new[] { Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer), Generator.RunWithGeneratedReferenceAssembly(library, ExternalContainer), Generator.RunWithGeneratedCompilationReference(library, ExternalContainer), })
			{
				await That(result.Diagnostics).IsEmpty();
				await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("await ((global::Lib.ICheck)created).CheckAsync()").Exactly(1)
					.Because("the container casts the public service the wrapper returns to the public convention the module recorded");
			}
		}

		[Test]
		public async Task OfAFactoryMemberReturningAnInternalImplementation_WithParameters_ForwardsThemThroughTheWrapper()
		{
			string library = Library(
				"public sealed class Drink : IDrink { }\ninternal sealed class Brew : IBrew { internal ValueTask WarmUpAsync(IDrink drink, CancellationToken cancellationToken) => default; }",
				MethodConvention + "[Singleton<Drink, IDrink>][Transient<IBrew>(Factory = nameof(Make))]",
				"public static partial class CoffeeModule { private static Brew Make() => new(); }");

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("Initializer_Brew_").Exactly(1).And.Contains("(created, Root.ResolveDrink(__s.__root), cancellationToken)")
				.Because("the container resolves the wrapper's parameters like the convention's, passing the resolve-time token");
		}

		[Test]
		public async Task OfAFactoryMemberReturningAnInternalImplementation_WithAConventionParameterOfAnInternalType_IsReportedByAContainerInAnotherAssembly()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult reference, GeneratorResult compilation, GeneratorResult friend) = RunAll(Library(
				"internal sealed class Settings { }\ninternal sealed class Brew : IBrew { internal Task WarmUpAsync(Settings settings) => Task.CompletedTask; }",
				MethodConvention + "[Singleton<Settings>][Singleton<IBrew>(Factory = nameof(Make))]",
				"public static partial class CoffeeModule { private static Brew Make() => new(); }"));
			const string expected = "error AWT234: 'Lib.IBrew' is initialized by the async initializer 'Lib.Brew.WarmUpAsync', but 'Lib.Brew' is not accessible from the container 'MyCode.MyContainer', and the module 'Lib.CoffeeModule' generated no wrapper for it because its parameter 'settings' has the type 'Lib.Settings', which is not public; make it public, or grant this assembly InternalsVisibleTo in its assembly";

			await That(library.Diagnostics).IsEmpty()
				.Because("a container in the module's assembly calls the factory and the convention itself");
			await That(same.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(external.Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("the module recorded that the convention initializes what its factory returns, and why it generated no wrapper for it");
			await That(reference.Diagnostics).HasSingle().Which.EndsWith(expected);
			await That(compilation.Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		[Arguments(false, "private static Brew Current { get; } = new();")]
		[Arguments(true, "internal static readonly Brew Current = new();")]
		public async Task OfAnInstanceMemberOfAnInternalImplementation_RunsNowhere(bool interfaceConvention, string instance)
		{
			(GeneratorResult built, GeneratorResult same, GeneratorResult other, GeneratorResult reference, GeneratorResult compilation, GeneratorResult friend) = RunAll(Library(
				Brew(publicImplementation: false, interfaceConvention),
				(interfaceConvention ? InterfaceConvention : MethodConvention) + "[Singleton<IBrew>(Instance = nameof(Current))]",
				$"public static partial class CoffeeModule {{ {instance} }}"));

			await That(built.Diagnostics).IsEmpty();
			foreach ((GeneratorResult result, string container) in new[] { (same, "Awaiten.Lib.SameContainer.g.cs"), (other, "Awaiten.MyCode.MyContainer.g.cs"), (reference, "Awaiten.MyCode.MyContainer.g.cs"), (compilation, "Awaiten.MyCode.MyContainer.g.cs"), (friend, "Awaiten.MyCode.MyContainer.g.cs"), })
			{
				await That(result.Diagnostics).IsEmpty();
				await That(result.Sources[container]).DoesNotContain("Initializer_").And.DoesNotContain(").WarmUpAsync(")
					.Because("a pre-built instance belongs to its owner, which initializes it, so no container runs a convention on it");
			}
		}

		[Test]
		public async Task ReachingOneMethodThroughTwoConventions_RunsItOnce()
		{
			string library = Library(
				"public abstract class Machine { internal virtual Task WarmUpAsync() => Task.CompletedTask; }\npublic sealed class Brew : Machine, IBrew { internal override Task WarmUpAsync() => Task.CompletedTask; }",
				"[AsyncInitializer<Machine>(nameof(Machine.WarmUpAsync))]" + MethodConvention + "[Singleton<Brew, IBrew>]");

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("Initializer_").Exactly(1)
				.Because("the override is the method the first convention runs, so the container runs it once although it sees neither");
		}

		[Test]
		public async Task ReachingOneInheritedMethodThroughABaseAndADerivedConvention_RunsItOnce()
		{
			string library = Library(
				"public abstract class Machine { internal Task WarmUpAsync() => Task.CompletedTask; }\npublic class Brew : Machine, IBrew { }\npublic sealed class Special : Brew { }",
				"[AsyncInitializer<Machine>(nameof(Machine.WarmUpAsync))][AsyncInitializer<Special>(nameof(Special.WarmUpAsync))][Singleton<Special, IBrew>]");

			foreach (GeneratorResult result in new[] { Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer), Generator.RunWithGeneratedCompilationReference(library, ExternalContainer), })
			{
				await That(result.Diagnostics).IsEmpty();
				await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("await global::Lib.CoffeeModule.Internals.Initializer_Machine_").Exactly(1)
					.And.DoesNotContain("Initializer_Special_")
					.Because("both conventions name the one method the special brew inherits, so the container runs it once");
			}
		}

		[Test]
		public async Task TwoMethodsOfOneName_RunEach()
		{
			string library = Library(
				"public interface ICheck { Task WarmUpAsync(); }\ninternal interface IClean<T> { Task WarmUpAsync(); }\npublic sealed class Brew : IBrew, ICheck, IClean<int>, IClean<string>\n{\n    Task ICheck.WarmUpAsync() => Task.CompletedTask;\n    Task IClean<int>.WarmUpAsync() => Task.CompletedTask;\n    Task IClean<string>.WarmUpAsync() => Task.CompletedTask;\n    internal Task WarmUpAsync() => Task.CompletedTask;\n}",
				"[AsyncInitializer<ICheck>(nameof(ICheck.WarmUpAsync))][AsyncInitializer<IClean<int>>(\"WarmUpAsync\")][AsyncInitializer<IClean<string>>(\"WarmUpAsync\")]" + MethodConvention + "[Singleton<Brew, IBrew>]");

			foreach (GeneratorResult result in new[] { Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer), Generator.RunWithGeneratedCompilationReference(library, ExternalContainer), })
			{
				string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
				await That(result.Diagnostics).IsEmpty();
				await That(source).Contains("await ((global::Lib.ICheck)created).WarmUpAsync()").Exactly(1);
				await That(source).Contains("await global::Lib.CoffeeModule.Internals.Initializer_IClean_").Exactly(2)
					.Because("each closing of the internal interface is a method of its own");
				await That(source).Contains("await global::Lib.CoffeeModule.Internals.Initializer_Brew_").Exactly(1)
					.Because("the internal method shares only its name with the interface methods, so it runs as well");
			}
		}

		[Test]
		public async Task WithParameters_ForwardsThemThroughTheWrapper()
		{
			string library = Library(
				"public sealed class Drink : IDrink { }\npublic sealed class Brew : IBrew { internal ValueTask WarmUpAsync(IDrink drink, CancellationToken cancellationToken) => default; }",
				MethodConvention + "[Singleton<Drink, IDrink>][Transient<Brew, IBrew>]");

			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer);

			await That(result.Diagnostics).IsEmpty();
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("(created, Root.ResolveDrink(__s.__root), cancellationToken)")
				.Because("the container resolves the wrapper's parameters like the convention's, passing the resolve-time token");
		}
	}

	public class Contributions
	{
		[Test]
		[Arguments("internal", "[Decorate<Logged, IBrew>]")]
		[Arguments("public", "[Decorate<Logged, IBrew>]")]
		[Arguments("internal", "[Composite<Logged, IBrew>]")]
		public async Task AnInternalConventionOfADecoratorOrComposite_RunsOnceInEveryContainer(string accessibility, string contribution)
		{
			string inner = contribution.StartsWith("[Decorate", StringComparison.Ordinal) ? "IBrew inner" : "System.Collections.Generic.IEnumerable<IBrew> inner";
			await AssertRunsOnceEverywhere(Library(
				$"public sealed class Brew : IBrew {{ }}\n{accessibility} sealed class Logged({inner}) : IBrew {{ public object Inner {{ get; }} = inner; internal Task WarmUpAsync() => Task.CompletedTask; }}",
				"[AsyncInitializer<Logged>(nameof(Logged.WarmUpAsync))][Singleton<Brew, IBrew>]" + contribution));
		}
	}

	public class OpenGenerics
	{
		private const string Consumer = """
			using Awaiten;
			using Lib;

			namespace MyCode;

			public sealed class Order(IRepository<int> repository) { public IRepository<int> Repository { get; } = repository; }

			[Container]
			[Import(typeof(CoffeeModule))]
			[Singleton<Order>]
			public static partial class MyContainer;
			""";

		[Test]
		[Arguments("internal", MethodConvention)]
		[Arguments("public", MethodConvention)]
		[Arguments("internal", InterfaceConvention)]
		[Arguments("public", InterfaceConvention)]
		public async Task AnInternalConvention_RunsOnceInEveryContainer(string accessibility, string convention)
		{
			string library = Library(
				$"public interface IRepository<T> {{ }}\npublic abstract class Brew {{ internal Task WarmUpAsync() => Task.CompletedTask; }}\n{accessibility} sealed class Repository<T> : {(convention == MethodConvention ? "Brew" : "IWarm")}, IRepository<T> {{ {(convention == MethodConvention ? string.Empty : "public Task WarmUpAsync() => Task.CompletedTask;")} }}",
				convention + "[Singleton(typeof(Repository<>), typeof(IRepository<>))]");
			(GeneratorResult built, GeneratorResult _, GeneratorResult external, GeneratorResult reference, GeneratorResult compilation, GeneratorResult friend) = RunAll(library, Consumer);

			await That(built.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			foreach (GeneratorResult result in new[] { external, reference, compilation, })
			{
				await That(result.Diagnostics).IsEmpty()
					.Because("the module generates the open factory, whose convention a container in another assembly runs through the wrapper");
				await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("await global::Lib.CoffeeModule.Internals.Initializer_").Exactly(1);
			}
		}
	}

	public class InternalServiceParameters
	{
		[Test]
		[Arguments("internal", "Settings settings")]
		[Arguments("public", "Settings settings")]
		[Arguments("internal", "System.Func<Settings> settings")]
		[Arguments("public", "System.Lazy<Settings> settings")]
		[Arguments("public", "System.Collections.Generic.IEnumerable<Settings> settings")]
		public async Task TheWrapperTakesTheServiceThroughItsHandle_AndRunsOnceInEveryContainer(string accessibility, string parameter)
		{
			string library = Library(
				$"internal sealed class Settings {{ }}\n{accessibility} sealed class Brew : IBrew {{ internal Task WarmUpAsync({parameter}, CancellationToken cancellationToken) => Task.CompletedTask; }}",
				MethodConvention + "[Singleton<Settings>][Singleton<Brew, IBrew>]");
			(GeneratorResult built, GeneratorResult same, GeneratorResult external, GeneratorResult reference, GeneratorResult compilation, GeneratorResult friend) = RunAll(library);

			await That(built.Diagnostics).IsEmpty()
				.Because("the module's wrapper takes the internal service through the handle the module generates for it");
			await That(built.Sources["Awaiten.ModuleScan.Lib.CoffeeModule.g.cs"]).Contains("Initializer_Brew_")
				.And.Contains("Hidden_Settings_");
			foreach (GeneratorResult result in new[] { same, external, reference, compilation, friend, })
			{
				await That(result.Diagnostics).IsEmpty();
			}

			foreach (GeneratorResult result in new[] { external, reference, compilation, })
			{
				await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("await global::Lib.CoffeeModule.Internals.Initializer_Brew_").Exactly(1)
					.And.DoesNotContain(").WarmUpAsync(")
					.Because("a container in another assembly runs the convention once through the module's wrapper and resolves the service's handle for it");
			}

			await That(same.Sources["Awaiten.Lib.SameContainer.g.cs"]).Contains(").WarmUpAsync(").Exactly(1)
				.And.DoesNotContain("Initializer_")
				.Because("a container in the module's assembly calls the convention directly, as before");
			await That(friend.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains(").WarmUpAsync(").Exactly(1)
				.And.DoesNotContain("Initializer_")
				.Because("a friend sees the internal convention and calls it directly, as before");
		}

		[Test]
		public async Task OnAScanMatch_RunsOnceInEveryContainer()
		{
			string library = Library(
				"internal sealed class Settings { }\ninternal sealed class Brew : IBrew { internal Task WarmUpAsync(Settings settings) => Task.CompletedTask; }",
				MethodConvention + "[Singleton<Settings>][Scan<IBrew>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]");
			(GeneratorResult built, GeneratorResult _, GeneratorResult external, GeneratorResult reference, GeneratorResult compilation, GeneratorResult friend) = RunAll(library);

			await That(built.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			foreach (GeneratorResult result in new[] { external, reference, compilation, })
			{
				await That(result.Diagnostics).IsEmpty();
				await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("await global::Lib.CoffeeModule.Internals.").Exactly(1)
					.Because("the container runs the convention of the scan match once, through a wrapper that takes the service's handle");
			}
		}

		[Test]
		public async Task AServiceOfAnotherModule_IsTakenThroughThatModulesHandle()
		{
			string library = """
				using Awaiten;
				using System.Threading.Tasks;

				namespace Lib;

				public interface IBrew { }
				internal sealed class Settings { }
				public sealed class Brew : IBrew { internal Task WarmUpAsync(Settings settings) => Task.CompletedTask; }

				[Module]
				[Singleton<Settings>]
				public static partial class SettingsModule;

				[Module]
				[AsyncInitializer<Brew>(nameof(Brew.WarmUpAsync))]
				[Singleton<Brew, IBrew>]
				public static partial class CoffeeModule;
				""";
			const string consumer = """
				using Awaiten;
				using Lib;

				namespace MyCode;

				[Container]
				[Import(typeof(SettingsModule))]
				[Import(typeof(CoffeeModule))]
				public static partial class MyContainer;
				""";

			GeneratorResult built = Generator.Run(library);
			GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, consumer);

			await That(built.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("the wrapper takes the internal service through the handle of the module that registers it");
			await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("await global::Lib.CoffeeModule.Internals.Initializer_Brew_").Exactly(1);
		}
	}

	public class Unreachable
	{
		[Test]
		public async Task ASelfCompiledImplementation_WithAConventionParameterOfAnUnregisteredInternalType_FailsTheLibrary()
		{
			GeneratorResult result = Generator.Run(Library(
				"internal sealed class Settings { }\ninternal sealed class Brew : IBrew { internal Task WarmUpAsync(Settings settings) => Task.CompletedTask; }",
				MethodConvention + "[Singleton<Brew, IBrew>]"));

			await That(result.Diagnostics).HasSingle().Which.EndsWith(
					"error AWT234: 'Lib.Brew' is initialized by the async initializer 'Lib.Brew.WarmUpAsync', but its parameter 'settings' has the type 'Lib.Settings', which is not public, so the module can generate no wrapper through which a container in another assembly runs it; make the parameter type public or register it in the module")
				.Because("the module builds the brew for every container in another assembly, and none of them could run its convention");
		}

		[Test]
		public async Task APublicImplementation_WithAConventionParameterOfAnUnregisteredInternalType_IsReportedByAContainerInAnotherAssembly()
		{
			string library = Library(
				"internal sealed class Settings { }\npublic sealed class Brew : IBrew { internal Task WarmUpAsync(Settings settings) => Task.CompletedTask; }",
				MethodConvention + "[Singleton<Brew, IBrew>]");
			(GeneratorResult _, GeneratorResult _, GeneratorResult external, GeneratorResult reference, GeneratorResult compilation, GeneratorResult _) = RunAll(library);
			const string expected = "error AWT234: 'Lib.Brew' is initialized by the async initializer 'Lib.Brew.WarmUpAsync', but the method is not accessible from the container 'MyCode.MyContainer', and the module 'Lib.CoffeeModule' generated no wrapper for it because its parameter 'settings' has the type 'Lib.Settings', which is not public; make the parameter type public or register it in the module, or grant this assembly InternalsVisibleTo in the module's assembly";

			await That(external.Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("a container in another assembly can call neither the convention nor a wrapper, and the module recorded why it generated none and what lets it");
			await That(reference.Diagnostics).HasSingle().Which.EndsWith(expected);
			await That(compilation.Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		public async Task AModuleThatIsNotPartial_IsReportedByAContainerInAnotherAssembly()
		{
			string library = Library(Brew(publicImplementation: true, interfaceConvention: false), MethodConvention + "[Singleton<Brew, IBrew>]", "public static class CoffeeModule;");

			await That(Generator.Run(library).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.EndsWith(
					"error AWT234: 'Lib.Brew' is initialized by the async initializer 'Lib.Brew.WarmUpAsync', but the method is not accessible from the container 'MyCode.MyContainer', and the module 'Lib.CoffeeModule' generated no wrapper for it because the module, or a class that contains it, is not partial; make the module and every class that contains it partial, or grant this assembly InternalsVisibleTo in the module's assembly")
				.Because("the module records that it is not partial, so a container in another assembly names why it cannot run the convention and asks for the partial modifier");
		}

		[Test]
		public async Task AModuleBuiltWithoutTheGenerator_IsReportedByAContainerInAnotherAssembly()
		{
			GeneratorResult result = Generator.RunWithReferencedAssembly(
				Library(Brew(publicImplementation: true, interfaceConvention: false), MethodConvention + "[Singleton<Brew, IBrew>]", "public static class CoffeeModule;"),
				ExternalContainer);

			await That(result.Diagnostics).HasSingle().Which.EndsWith(
					"error AWT234: 'Lib.Brew' is initialized by the async initializer 'Lib.Brew.WarmUpAsync', but the method is not accessible from the container 'MyCode.MyContainer', and the module 'Lib.CoffeeModule' generated no wrapper for it; make it public, or grant this assembly InternalsVisibleTo in its assembly")
				.Because("the container does not even see the internal method, but it must not leave the convention out silently");
		}
	}
}
