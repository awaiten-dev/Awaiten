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
		public interface IClock { }
		public sealed class Clock : IClock { }
		internal sealed class Greeter : IGreeter { }

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
	public async Task OfAPublicService_IsCalledThroughTheWrapperByEveryContainer(string registration, string member)
	{
		(GeneratorResult library, GeneratorResult same, GeneratorResult external, GeneratorResult friend) = RunAll(Library("public static partial", registration, member));

		await That(library.Diagnostics).IsEmpty();
		await That(same.Diagnostics).IsEmpty()
			.Because("a container in the module's assembly calls the wrapper of a member it cannot reach");
		await That(external.Diagnostics).IsEmpty()
			.Because("the module wraps a private member of a public service, which no container could call otherwise");
		await That(friend.Diagnostics).IsEmpty();
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
}
