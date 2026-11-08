namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A private <c>Factory</c> or <c>Instance</c> member of a module: every container calls it through the wrapper the
///     module generates, and a module that cannot receive the wrapper reports it once in its own build.
/// </summary>
public class ModulePrivateMemberTests
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

	private static (GeneratorResult Library, GeneratorResult Same, GeneratorResult External, GeneratorResult Friend) RunAll(string library)
		=> (Generator.Run(library),
			Generator.Run(library + SameAssemblyContainer),
			Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer),
			Generator.RunWithGeneratedReferencedAssembly(library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;"), ExternalContainer));

	private static string Library(string modifiers, string registration, string member) => $$"""
		using Awaiten;

		namespace Lib;

		public interface IGreeter { }
		public interface IOther { }
		public interface IClock { }
		public sealed class Clock : IClock { }
		internal interface ISecret { }
		internal sealed class Secret : ISecret { }
		internal sealed class Greeter : IGreeter, IOther { }

		[Module]
		[Singleton<Clock, IClock>]
		{{registration}}
		{{modifiers}} class CoffeeModule
		{
			{{member}}
		}
		""";

	[Test]
	[Arguments("[Singleton<IGreeter>(Factory = \"Factory\")]", "private static IGreeter Factory() => new Greeter();")]
	[Arguments("[Singleton<IGreeter>(Factory = nameof(Make))]", "private static IGreeter Make(IClock clock) => new Greeter();")]
	[Arguments("[Singleton<IGreeter>(Instance = nameof(Shared))]", "private static IGreeter Shared { get; } = new Greeter();")]
	[Arguments("[Singleton<Greeter, IGreeter>(Factory = nameof(Make))]", "private static Greeter Make() => new Greeter();")]
	[Arguments("[Singleton<Greeter, IGreeter>(Instance = nameof(Shared))]", "private static Greeter Shared { get; } = new Greeter();")]
	public async Task OfAPublicService_IsCalledThroughTheWrapperByEveryContainer(string registration, string member)
	{
		(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library("public static partial", registration, member));

		await That(library.Diagnostics).IsEmpty();
		await That(same.Diagnostics).IsEmpty()
			.Because("a container in the module's assembly calls the wrapper of a member it cannot reach");
		await That(external.Diagnostics).IsEmpty()
			.Because("the module wraps a private member of a public service, which no container could call otherwise");
		await That(friend.Diagnostics).IsEmpty()
			.Because("a friend names an internal implementation, so it calls a wrapper that returns it rather than the public service");
		await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Internals.");
		await That(same.Sources["Awaiten.Lib.SameContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Internals.");
	}

	[Test]
	[Arguments("public static")]
	[Arguments("internal static")]
	public async Task OfANonPartialModule_ReportsAwt194InTheLibraryOnce(string modifiers)
	{
		string library = Library(modifiers, "[Singleton<IGreeter>(Factory = nameof(Make))]", "private static IGreeter Make() => new Greeter();");

		GeneratorResult alone = Generator.Run(library);
		GeneratorResult same = Generator.Run(library + SameAssemblyContainer);

		await That(alone.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT194") && diagnostic.Contains("'Lib.CoffeeModule' produces 'Lib.IGreeter' through its private member 'Make'"))
			.Because("no container can call the private member, and the module cannot receive the wrapper that would, so the library reports it before any consumer");
		await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT194"))
			.Because("the container in the module's assembly leaves the one cause to the module instead of adding AWT153");
	}

	[Test]
	[Arguments("[Singleton<Greeter, IGreeter>(Factory = nameof(Make))][Singleton<Greeter, IOther>(Factory = nameof(Make))]", "private static Greeter Make() => new Greeter();")]
	[Arguments("[Singleton<Greeter, IGreeter>(Factory = nameof(Make))][Singleton<Greeter, IOther>(Factory = nameof(Make))]", "internal static Greeter Make() => new Greeter();")]
	[Arguments("[Singleton<Greeter, IGreeter>(Instance = nameof(Shared))][Singleton<Greeter, IOther>(Instance = nameof(Shared))]", "private static Greeter Shared { get; } = new Greeter();")]
	[Arguments("[Singleton<IGreeter>(Factory = nameof(Make))][Singleton<Secret, ISecret>]", "private static IGreeter Make(ISecret secret) => new Greeter();")]
	[Arguments("[Singleton<IGreeter>(Factory = nameof(Make))][Singleton<Secret, ISecret>]", "internal static IGreeter Make(ISecret secret) => new Greeter();")]
	public async Task NoPublicWrapperCanServe_IsBuiltThroughAHandleForAContainerInAnotherAssembly(string registration, string member)
	{
		(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library("public static partial", registration, member));

		await That(library.Diagnostics).IsEmpty();
		await That(same.Diagnostics).IsEmpty();
		await That(external.Diagnostics).IsEmpty()
			.Because("no public wrapper can return an internal implementation of several services or take an internal parameter, so the module builds it through an instance handle");
		await That(friend.Diagnostics).IsEmpty();
		await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.CoffeeModule.Internals.Alias_");
	}

	[Test]
	[Arguments("public static partial")]
	[Arguments("internal static partial")]
	public async Task OverloadedPrivateFactory_IsReportedAsAmbiguousOnceInTheLibrary(string modifiers)
	{
		string library = Library(modifiers, "[Singleton<IGreeter>(Factory = nameof(Make))]", "private static IGreeter Make() => new Greeter(); private static IGreeter Make(IClock clock) => new Greeter();");

		GeneratorResult alone = Generator.Run(library);
		GeneratorResult same = Generator.Run(library + SameAssemblyContainer);

		await That(alone.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT112") && diagnostic.Contains("more than one accessible method 'Make'"))
			.Because("no wrapper can choose between the private overloads, so the library reports the ambiguity before any consumer does");
		await That(same.Diagnostics).HasSingle().Matching(diagnostic => diagnostic.Contains("AWT112"))
			.Because("the container in the module's assembly leaves the one cause to the module instead of adding AWT153");
	}
}
