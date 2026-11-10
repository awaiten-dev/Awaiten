namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A mistake of a module's <c>[Decorate]</c> that is wrong for every importer: the module's own build reports it,
///     and a container that imports the module does not repeat it, unless the module was built without a generator that does.
/// </summary>
public class ModuleDecoratorMistakeTests
{
	private const string ExternalContainer = """
		using Awaiten;
		using Lib;

		namespace MyCode;

		[Container]
		[Import(typeof(KettleModule))]
		public static partial class MyContainer;
		""";

	private const string SameAssemblyContainer = """

		[Container]
		[Import(typeof(KettleModule))]
		public static partial class SameContainer;
		""";

	private const string Decorator = "public sealed class LoggingKettle : IKettle { public LoggingKettle(IKettle inner) { } }";

	private static string Library(string decorate, string decorator = Decorator, string modifiers = "public static partial") => $$"""
		using Awaiten;

		namespace Lib;

		public interface IKettle { }
		public sealed class Kettle : IKettle { }
		public interface IHandler<T> { }
		public sealed class Handler : IHandler<int> { }
		public sealed class LoggingHandler<T> : IHandler<T> { public LoggingHandler(IHandler<T> inner) { } }
		{{decorator}}

		[Module]
		[Singleton<Kettle, IKettle>]
		[Singleton<Handler, IHandler<int>>]
		{{decorate}}
		{{modifiers}} class KettleModule;
		""";

	private static string Friend(string library)
		=> library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;");

	[Test]
	[Arguments("[Decorate<LoggingKettle, IKettle>(Keys = new object[0])]", Decorator, "AWT123", "with an empty Keys")]
	[Arguments("[Decorate(typeof(LoggingHandler<>), typeof(IHandler<>), Keys = new object[0])]", Decorator, "AWT123", "with an empty Keys")]
	[Arguments("[Decorate<LoggingKettle, IKettle>(Keys = new object[] { null }, AllKeys = true)]", Decorator, "AWT211", "sets both Keys and AllKeys")]
	[Arguments("[Decorate(typeof(LoggingHandler<>), typeof(IHandler<>), Keys = new object[] { null }, AllKeys = true)]", Decorator, "AWT211", "sets both Keys and AllKeys")]
	[Arguments("[Decorate<LoggingKettle, IKettle>(Keys = new object[] { 1 })]", Decorator, "AWT170", "unsupported key type 'int'")]
	[Arguments("[Decorate<LoggingKettle, IKettle>]", "public sealed class LoggingKettle : IKettle { }", "AWT124", "no single constructor parameter that receives the inner")]
	[Arguments("[Decorate<LoggingKettle, IKettle>]", "public sealed class LoggingKettle : IKettle { public LoggingKettle(IKettle a, IKettle b) { } }", "AWT124", "no single constructor parameter that receives the inner")]
	[Arguments("[Decorate<LoggingKettle, IKettle>]", "internal sealed class LoggingKettle : IKettle { internal LoggingKettle() { } }", "AWT124", "no single constructor parameter that receives the inner")]
	public async Task IsReportedByTheModuleAndNotRepeatedByItsImporters(string decorate, string decorator, string id, string fragment)
	{
		string library = Library(decorate, decorator);

		GeneratorResult alone = Generator.Run(library);
		GeneratorResult same = Generator.Run(library + SameAssemblyContainer);
		GeneratorResult friend = Generator.RunWithGeneratedReferencedAssembly(Friend(library), ExternalContainer);
		GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer);
		GeneratorResult reference = Generator.RunWithGeneratedReferenceAssembly(library, ExternalContainer);

		await That(alone.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains(id) && diagnostic.Contains(fragment) && diagnostic.StartsWith("(15,2)"))
			.Because("the mistake is wrong for every importer, so the library reports it at the [Decorate] before any consumer does");
		await That(same.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains(id) && diagnostic.StartsWith("(15,2)"))
			.Because("a container of the module's assembly leaves the mistake to the module");
		await That(friend.Diagnostics).IsEmpty()
			.Because("the module recorded that it reports the mistake itself");
		await That(external.Diagnostics).IsEmpty();
		await That(reference.Diagnostics).IsEmpty()
			.Because("the record is an internal type, which a reference assembly keeps");
	}

	[Test]
	[Arguments("[Decorate<LoggingKettle, IKettle>(Keys = new object[0])]", Decorator, "AWT123")]
	[Arguments("[Decorate<LoggingKettle, IKettle>(Keys = new object[] { null }, AllKeys = true)]", Decorator, "AWT211")]
	[Arguments("[Decorate<LoggingKettle, IKettle>(Keys = new object[] { 1 })]", Decorator, "AWT170")]
	[Arguments("[Decorate<LoggingKettle, IKettle>]", "public sealed class LoggingKettle : IKettle { }", "AWT124")]
	public async Task OfAModuleBuiltWithoutTheGenerator_IsReportedByTheImporter(string decorate, string decorator, string id)
	{
		GeneratorResult result = Generator.RunWithReferencedAssembly(Library(decorate, decorator), ExternalContainer);

		await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains(id) && diagnostic.StartsWith("(7,2)"))
			.Because("a module built by an older generator records nothing, so the importer still reports the mistake at its [Import]");
	}

	[Test]
	[Arguments("public static")]
	[Arguments("internal static")]
	public async Task OfAModuleThatIsNotPartial_IsReportedByTheModule(string modifiers)
	{
		string library = Library("[Decorate<LoggingKettle, IKettle>(Keys = new object[0])]", modifiers: modifiers);

		GeneratorResult alone = Generator.Run(library);
		GeneratorResult same = Generator.Run(library + SameAssemblyContainer);

		await That(alone.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT123"));
		await That(same.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT123"));
	}

	[Test]
	public async Task OfANonPartialPublicModule_IsNotRepeatedByAnImporter()
	{
		string library = Library("[Decorate<LoggingKettle, IKettle>(Keys = new object[0])]", modifiers: "public static");

		GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer);

		await That(external.Diagnostics).IsEmpty()
			.Because("the record is a type next to the module, which a module that is not partial carries too");
	}

	[Test]
	public async Task WithSomeKeysSupported_DecoratesTheSupportedKeys()
	{
		string library = Library("[Decorate<LoggingKettle, IKettle>(Keys = new object[] { 1, null })]");

		GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer);

		await That(external.Diagnostics).IsEmpty()
			.Because("the importer leaves the unsupported key to the module and decorates the unkeyed registration it names too");
		await That(external.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("global::Lib.LoggingKettle(");
	}

	[Test]
	[Arguments("public sealed class LoggingKettle : IKettle { public LoggingKettle() { } public LoggingKettle(IKettle inner) { } }")]
	[Arguments("public sealed class LoggingKettle : IKettle { public LoggingKettle(object inner) { } }")]
	public async Task WithAConstructorThatReceivesTheInner_IsNotReportedByTheModule(string decorator)
	{
		string library = Library("[Decorate<LoggingKettle, IKettle>]", decorator);

		GeneratorResult alone = Generator.Run(library);
		GeneratorResult external = Generator.RunWithGeneratedReferencedAssembly(library, ExternalContainer);

		await That(alone.Diagnostics).IsEmpty()
			.Because("a container may select the constructor that receives the inner instance, so only it can tell");
		await That(external.Diagnostics).IsEmpty();
	}
}
