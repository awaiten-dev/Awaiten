using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A self-compiled module registration that takes an internal service of its module as a factory with runtime
///     arguments: the library alone, a container in the library's assembly, one in another assembly and one the library
///     grants <c>InternalsVisibleTo</c> accept and reject the same shapes.
/// </summary>
public class ModuleHiddenRuntimeArgumentTests
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

	private static uint Fnv1a(string text)
	{
		uint hash = 2166136261;
		foreach (char character in text)
		{
			hash = unchecked((hash ^ character) * 16777619);
		}

		return hash;
	}

	private static string Handle(string service)
		=> $"global::Lib.CoffeeModule.Internals.Hidden_{service[(service.LastIndexOf('.') + 1)..]}_{Fnv1a(service):x8}";

	private static string Factory(string implementation)
		=> $"Registration_{implementation[(implementation.LastIndexOf('.') + 1)..]}_{Fnv1a(implementation):x8}";

	private static string ModuleSource(GeneratorResult result)
		=> result.Sources.Single(source => source.Key.Contains("ModuleScan")).Value;

	private static (GeneratorResult Library, GeneratorResult Same, GeneratorResult External, GeneratorResult Friend) RunAll(string library)
		=> (Generator.Run(library),
			Generator.Run(library + SameAssemblyContainer),
			Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer),
			Generator.RunWithGeneratedReferencedAssembly(library.Replace("using Awaiten;", "using Awaiten;\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]"), ExternalContainer));

	private static string Library(string parameter, string brew = "internal sealed class Brew : IBrew { public Brew([Arg] IOrder order) { } }", string registrations = "[Transient<Brew, IBrew>]", string members = "", string lifetime = "Transient") => $$"""
		using System;
		using System.Threading.Tasks;
		using Awaiten;

		namespace Lib;

		public interface IOrder { }
		public interface IBarista { }
		internal interface IBrew { }
		{{brew}}
		internal sealed class Barista : IBarista { public Barista({{parameter}} brew) { } }

		[Module]
		{{registrations}}
		[{{lifetime}}<Barista, IBarista>]
		public static partial class CoffeeModule { {{members}} }
		""";

	public class SupportedShapes
	{
		[Test]
		[Arguments("Func<IOrder, IBrew>")]
		[Arguments("Func<IOrder, Owned<IBrew>>")]
		public async Task AreAcceptedByEveryContainer(string parameter)
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(parameter));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("the generated factory takes a factory of the handle with the same runtime arguments and adapts it");
			await That(friend.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task TheFactoryTakesAFactoryOfTheHandleAndAdaptsIt()
		{
			string handle = Handle("global::Lib.IBrew");
			GeneratorResult library = Generator.Run(Library("Func<IOrder, IBrew>"));

			await That(ModuleSource(library)).Contains($"public static global::Lib.IBarista {Factory("global::Lib.Barista")}(global::System.Func<global::Lib.IOrder, {handle}> @brew) => new global::Lib.Barista((awaiten__arg1) => @brew(awaiten__arg1).Value);")
				.Because("the container passes a factory of the handle and the generated factory unwraps what each call returns");
		}

		[Test]
		public async Task OfOwned_TheFactoryMapsTheOwnedHandle()
		{
			string handle = Handle("global::Lib.IBrew");
			GeneratorResult library = Generator.Run(Library("Func<IOrder, Owned<IBrew>>"));

			await That(ModuleSource(library)).Contains($"(global::System.Func<global::Lib.IOrder, global::Awaiten.Owned<{handle}>> @brew) => new global::Lib.Barista((awaiten__arg1) => global::Awaiten.Internals.AwaitenOwnedProjection.Map(@brew(awaiten__arg1), awaiten__handle => awaiten__handle.Value));")
				.Because("the owned handle keeps the disposal of the built instance with its caller");
		}

		[Test]
		public async Task OfOwnedHeldByASingleton_ReportsNoAwt118InAnyContainer()
		{
			string library = Library(
				"Func<IOrder, Owned<IBrew>>",
				"internal sealed class Brew : IBrew, IDisposable { public Brew([Arg] IOrder order) { } public void Dispose() { } }",
				lifetime: "Singleton");

			await That(await Analyzer.Run<AwaitenAnalyzer>(library + SameAssemblyContainer)).IsEmpty();
			await That(await Analyzer.RunWithReferencedAssembly<AwaitenAnalyzer>(library, ExternalContainer)).IsEmpty()
				.Because("an owned factory hands each brew back with its disposal, through a handle too");
			await That(await Analyzer.RunWithReferencedAssembly<AwaitenAnalyzer>(library.Replace("using Awaiten;", "using Awaiten;\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]"), ExternalContainer)).IsEmpty();
		}

		[Test]
		public async Task TheAliasPassesTheArgumentsToAFactoryOfTheInstanceHandle()
		{
			string handle = Handle("global::Lib.IBrew");
			string instance = $"global::Lib.CoffeeModule.Internals.HiddenInstance_Brew_{Fnv1a("global::Lib.Brew"):x8}";
			GeneratorResult library = Generator.Run(Library("Func<IOrder, IBrew>"));

			await That(ModuleSource(library)).Contains($"(global::System.Func<global::Lib.IOrder, {instance}> @awaiten__handle, [global::Awaiten.Arg] global::Lib.IOrder @awaiten__arg1) => new {handle}(@awaiten__handle(@awaiten__arg1).Value);")
				.Because("a service built from runtime arguments is reached only through a factory, so the alias forwards them to the instance handle's");
		}

		[Test]
		public async Task OfAnImplementationUnderSeveralPublicServices_IsAcceptedByEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll("""
				using System;
				using Awaiten;

				namespace Lib;

				public interface IOrder { }
				public interface IBrew { }
				public interface IReceipt { }
				public interface IBarista { }
				internal sealed class Brew : IBrew, IReceipt { public Brew([Arg] IOrder order) { } }
				internal sealed class Barista : IBarista { public Barista(Func<IOrder, IBrew> brew, Func<IOrder, IReceipt> receipt) { } }

				[Module]
				[Transient<Brew, IBrew>]
				[Transient<Brew, IReceipt>]
				[Transient<Barista, IBarista>]
				public static partial class CoffeeModule { }
				""");

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("each public service of an implementation with [Arg] parameters is an alias that forwards the runtime arguments to its instance handle");
			await That(friend.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task WithSeveralArguments_IsAcceptedByEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(
				"Func<IOrder, int, string, IBrew>",
				"internal sealed class Brew : IBrew { public Brew([Arg] IOrder order, [Arg] int shots, [Arg] string size) { } }"));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(ModuleSource(library)).Contains("(awaiten__arg1, awaiten__arg2, awaiten__arg3) => @brew(awaiten__arg1, awaiten__arg2, awaiten__arg3).Value")
				.Because("every runtime argument is passed through in order");
		}

		[Test]
		public async Task ThroughAFactoryMemberWithAnArg_IsAcceptedByEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(
				"Func<IOrder, IBrew>",
				"internal sealed class Brew : IBrew { public Brew(IOrder order) { } }",
				"[Transient<IBrew>(Factory = nameof(MakeBrew))]",
				"private static IBrew MakeBrew([Arg] IOrder order) => new Brew(order);"));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("the wrapper of the factory member forwards the runtime argument into the handle");
			await That(friend.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task WithAKey_IsAcceptedByEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(
				"[FromKey(\"strong\")] Func<IOrder, IBrew>",
				registrations: "[Transient<Brew, IBrew>(Key = \"strong\")]"));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("the factory takes the keyed handle's factory");
			await That(friend.Diagnostics).IsEmpty();
		}
	}

	public class OtherConsumers
	{
		private static string Library(string consumer, string registration, string members = "") => $$"""
			using System;
			using Awaiten;

			namespace Lib;

			public interface IOrder { }
			public interface IBarista { }
			internal interface IBrew { }
			internal sealed class Brew : IBrew { public Brew([Arg] IOrder order) { } }
			internal sealed class Barista : IBarista { }
			{{consumer}}

			[Module]
			[Transient<Brew, IBrew>]
			[Scoped<Barista, IBarista>]
			{{registration}}
			public static partial class CoffeeModule { {{members}} }
			""";

		[Test]
		[Arguments("internal sealed class LoggingBarista : IBarista { public LoggingBarista(IBarista inner, Func<IOrder, IBrew> brew) { } }", "[Decorate<LoggingBarista, IBarista>]")]
		[Arguments("internal sealed class LoggingBarista : IBarista { public LoggingBarista(IBarista inner, Func<IOrder, Owned<IBrew>> brew) { } }", "[Decorate<LoggingBarista, IBarista>]")]
		[Arguments("internal sealed class AllBaristas : IBarista { public AllBaristas(System.Collections.Generic.IEnumerable<IBarista> all, Func<IOrder, IBrew> brew) { } }", "[Composite<AllBaristas, IBarista>]")]
		[Arguments("internal sealed class Till : IBarista { [Inject] public Func<IOrder, IBrew> Brew { get; set; } = null!; }", "[Transient<Till, IBarista>(Key = \"till\")]")]
		public async Task AreAcceptedByEveryContainer(string consumer, string registration)
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(consumer, registration));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("a decorator, a composite and an injected property take the factory of the handle like a constructed registration");
			await That(friend.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task AHookTakingIt_IsAcceptedByEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(
				"internal sealed class HookedBarista : IBarista { }",
				"[Transient<HookedBarista, IBarista>(Key = \"hooked\", OnActivated = nameof(Activated))]",
				"internal static void Activated(HookedBarista barista, Func<IOrder, IBrew> brew) { }"));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("the hook's wrapper takes the factory of the handle like the hook takes the factory of the service");
			await That(friend.Diagnostics).IsEmpty();
		}
	}

	public class AsyncInitialized
	{
		private static string Library(string services) => $$"""
			using System;
			using System.Threading.Tasks;
			using Awaiten;

			namespace Lib;

			public interface IOrder { }
			{{services}}
			internal sealed class Brew : IBrew, IReceipt { public Brew([Arg] IOrder order) { } public Task StartAsync() => Task.CompletedTask; }

			[Module]
			[AsyncInitializer<Brew>(nameof(Brew.StartAsync))]
			[Transient<Brew, IBrew>]
			[Transient<Brew, IReceipt>]
			public static partial class CoffeeModule { }
			""";

		private static (GeneratorResult Same, GeneratorResult External, GeneratorResult Friend) RunContainers(string library, string container)
			=> (Generator.Run(library + string.Join("\n", container.Split('\n').Where(line => !line.StartsWith("using ", StringComparison.Ordinal) && !line.StartsWith("namespace ", StringComparison.Ordinal)))),
				Generator.RunWithGeneratedReferencedAssembly(library, container),
				Generator.RunWithGeneratedReferencedAssembly(library.Replace("using Awaiten;", "using Awaiten;\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]"), container));

		[Test]
		public async Task UnderSeveralPublicServices_CompilesInEveryContainer()
		{
			string library = Library("public interface IBrew { }\npublic interface IReceipt { }");

			(GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunContainers(library, ExternalContainer);

			await That(Generator.Run(library).Diagnostics).IsEmpty();
			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("the alias of an async-initialized implementation built from runtime arguments awaits its instance handle");
			await That(friend.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task AnAsyncFactoryOfTheContainer_IsAcceptedByEveryContainer()
		{
			string library = Library("public interface IBrew { }\ninternal interface IReceipt { }");
			const string container = """
				using System;
				using System.Threading.Tasks;
				using Awaiten;
				using Lib;

				namespace MyCode;

				public sealed class Shop { public Shop(Func<IOrder, Task<IBrew>> brew) { } }

				[Container]
				[Import(typeof(CoffeeModule))]
				[Transient<Shop>]
				public static partial class MyContainer;
				""";

			(GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunContainers(library, container);

			await That(same.Diagnostics).IsEmpty();
			await That(external.Diagnostics).IsEmpty()
				.Because("an async factory awaits the initialization behind the handle, as in the library's own assembly");
			await That(friend.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task ThroughAnAsyncInitializedDependency_IsAcceptedByAnAsyncFactoryAndReportsAwt120ForASynchronousOne()
		{
			const string library = """
				using System;
				using System.Threading.Tasks;
				using Awaiten;

				namespace Lib;

				public interface IOrder { }
				public interface IBrew { }
				internal interface IClock { }
				internal sealed class Clock : IClock { public Task StartAsync() => Task.CompletedTask; }
				internal sealed class Brew : IBrew { public Brew(IClock clock, [Arg] IOrder order) { } }

				[Module]
				[AsyncInitializer<Clock>(nameof(Clock.StartAsync))]
				[Singleton<Clock, IClock>]
				[Transient<Brew, IBrew>]
				public static partial class CoffeeModule { }
				""";
			const string container = """
				using System;
				using System.Threading.Tasks;
				using Awaiten;
				using Lib;

				namespace MyCode;

				public sealed class Shop { public Shop(Func<IOrder, Task<IBrew>> brew) { } }
				public sealed class Till { public Till(Func<IOrder, IBrew> brew) { } }

				[Container]
				[Import(typeof(CoffeeModule))]
				[Transient<Shop>]
				[Transient<Till>]
				public static partial class MyContainer;
				""";

			(GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunContainers(library, container);

			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT120"));
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT120") && diagnostic.Contains("Lib.Brew"))
				.Because("the alias awaits a handle that is async-tainted through its dependency, so only the synchronous factory is rejected");
			await That(friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT120"));
		}

		[Test]
		public async Task ASynchronousFactoryOfTheContainer_ReportsAwt119InEveryContainer()
		{
			string library = Library("public interface IBrew { }\ninternal interface IReceipt { }");
			const string container = """
				using System;
				using Awaiten;
				using Lib;

				namespace MyCode;

				public sealed class Shop { public Shop(Func<IOrder, IBrew> brew) { } }

				[Container]
				[Import(typeof(CoffeeModule))]
				[Transient<Shop>]
				public static partial class MyContainer;
				""";

			(GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunContainers(library, container);

			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT119"));
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT119"))
				.Because("a synchronous factory cannot await the initialization behind the handle");
			await That(friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT119"));
		}
	}

	public class Rejections
	{
		[Test]
		public async Task AMismatchedArgument_ReportsAwt113InEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library("Func<int, IBrew>"));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT113"));
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT113"))
				.Because("the container matches the runtime arguments of the factory against the [Arg] parameters behind the handle");
			await That(friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT113"));
		}

		[Test]
		public async Task ANonTransientService_ReportsAwt114InEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library("Func<IOrder, IBrew>", registrations: "[Scoped<Brew, IBrew>]"));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT114"));
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT114"))
				.Because("a service built from runtime arguments must be transient behind a handle too");
			await That(friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT114"));
		}

		[Test]
		public async Task ANonTransientServiceUnderSeveralServices_ReportsAwt114OnceInEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(
				"Func<IOrder, IBrew>",
				"public interface IReceipt { }\ninternal sealed class Brew : IBrew, IReceipt { public Brew([Arg] IOrder order) { } }",
				"[Scoped<Brew, IBrew>]\n[Scoped<Brew, IReceipt>]"));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT114"));
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT114") && diagnostic.Contains("'Lib.Brew'"))
				.Because("the instance handle reports its lifetime once, and the aliases that serve its services share it");
			await That(friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT114"));
		}

		[Test]
		public async Task ADisposableServiceHeldByASingleton_ReportsAwt118InEveryContainer()
		{
			string library = Library(
				"Func<IOrder, IBrew>",
				"internal sealed class Brew : IBrew, IDisposable { public Brew([Arg] IOrder order) { } public void Dispose() { } }",
				lifetime: "Singleton");

			string[] same = await Analyzer.Run<AwaitenAnalyzer>(library + SameAssemblyContainer);
			string[] external = await Analyzer.RunWithReferencedAssembly<AwaitenAnalyzer>(library, ExternalContainer);
			string[] friend = await Analyzer.RunWithReferencedAssembly<AwaitenAnalyzer>(library.Replace("using Awaiten;", "using Awaiten;\n[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]"), ExternalContainer);

			await That(same).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT118"));
			await That(external).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT118"))
				.Because("each call of the factory a singleton holds strands a disposable on the root, through a handle too");
			await That(friend).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT118"));
		}

		[Test]
		public async Task AnAsyncInitializedService_ReportsAwt119InEveryContainer()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library(
				"Func<IOrder, IBrew>",
				"internal sealed class Brew : IBrew { public Brew([Arg] IOrder order) { } public Task StartAsync() => Task.CompletedTask; }",
				"[AsyncInitializer<Brew>(nameof(Brew.StartAsync))]\n[Transient<Brew, IBrew>]"));

			await That(library.Diagnostics).IsEmpty();
			await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT119"));
			await That(external.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT119"))
				.Because("a synchronous factory cannot await the initialization behind the handle either");
			await That(friend.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT119"));
		}

		[Test]
		public async Task AnArgumentTypeOtherAssembliesCannotName_ReportsAwt195()
		{
			GeneratorResult library = Generator.Run(Library(
				"Func<Secret, IBrew>",
				"internal sealed class Secret { }\ninternal sealed class Brew : IBrew { public Brew([Arg] IOrder order) { } }"));

			await That(library.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT195") && diagnostic.Contains("'brew'") && diagnostic.Contains("'System.Func<Lib.Secret, Lib.IBrew>'"))
				.Because("a container in another assembly cannot name the runtime argument the factory takes");
		}

		[Test]
		[Arguments("Func<IOrder, Task<IBrew>>", "System.Func<Lib.IOrder, System.Threading.Tasks.Task<Lib.IBrew>>")]
		[Arguments("Func<IOrder, Lazy<IBrew>>", "System.Func<Lib.IOrder, System.Lazy<Lib.IBrew>>")]
		public async Task AnUnsupportedResult_ReportsAwt237(string parameter, string display)
		{
			GeneratorResult library = Generator.Run(Library(parameter));

			await That(library.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT237") && diagnostic.Contains($"'{display}'"))
				.Because("the generated factory adapts only a factory that returns the service or an Owned<T> of it");
		}
	}
}
