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

	public class Unreachable
	{
		[Test]
		public async Task ASelfCompiledImplementation_WithAConventionParameterOfAnInternalType_FailsTheLibrary()
		{
			GeneratorResult result = Generator.Run(Library(
				"internal sealed class Settings { }\ninternal sealed class Brew : IBrew { internal Task WarmUpAsync(Settings settings) => Task.CompletedTask; }",
				MethodConvention + "[Singleton<Settings>][Singleton<Brew, IBrew>]"));

			await That(result.Diagnostics).HasSingle().Which.EndsWith(
					"error AWT234: 'Lib.Brew' is initialized by the async initializer 'Lib.Brew.WarmUpAsync', but its parameter 'settings' has the type 'Lib.Settings', which is not public, so the module can generate no wrapper through which a container in another assembly runs it; make the parameter type public")
				.Because("the module builds the brew for every container in another assembly, and none of them could run its convention");
		}

		[Test]
		public async Task APublicImplementation_WithAConventionParameterOfAnInternalType_IsReportedByAContainerInAnotherAssembly()
		{
			(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult reference, GeneratorResult compilation, GeneratorResult friend) = RunAll(Library(
				"internal sealed class Settings { }\npublic sealed class Brew : IBrew { internal Task WarmUpAsync(Settings settings) => Task.CompletedTask; }",
				MethodConvention + "[Singleton<Settings>][Singleton<Brew, IBrew>]"));
			const string expected = "error AWT234: 'Lib.Brew' is initialized by the async initializer 'Lib.Brew.WarmUpAsync', but the method is not accessible from the container 'MyCode.MyContainer', and the module 'Lib.CoffeeModule' generated no wrapper for it because its parameter 'settings' has the type 'Lib.Settings', which is not public; make it public, or grant this assembly InternalsVisibleTo in its assembly";

			await That(library.Diagnostics).IsEmpty()
				.Because("a container in the module's assembly constructs the public brew and calls the convention itself");
			await That(same.Diagnostics).IsEmpty();
			await That(friend.Diagnostics).IsEmpty();
			await That(external.Diagnostics).HasSingle().Which.EndsWith(expected)
				.Because("a container in another assembly can call neither the convention nor a wrapper, and the module recorded why it generated none");
			await That(reference.Diagnostics).HasSingle().Which.EndsWith(expected);
			await That(compilation.Diagnostics).HasSingle().Which.EndsWith(expected);
		}

		[Test]
		public async Task AModuleThatIsNotPartial_IsReportedByAContainerInAnotherAssembly()
		{
			string library = Library(Brew(publicImplementation: true, interfaceConvention: false), MethodConvention + "[Singleton<Brew, IBrew>]", "public static class CoffeeModule;");

			await That(Generator.Run(library).Diagnostics).IsEmpty();
			await That(Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer).Diagnostics).HasSingle().Which.EndsWith(
					"error AWT234: 'Lib.Brew' is initialized by the async initializer 'Lib.Brew.WarmUpAsync', but the method is not accessible from the container 'MyCode.MyContainer', and the module 'Lib.CoffeeModule' generated no wrapper for it because the module, or a class that contains it, is not partial; make it public, or grant this assembly InternalsVisibleTo in its assembly")
				.Because("the module records that it is not partial, so a container in another assembly names why it cannot run the convention instead of leaving it out");
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
