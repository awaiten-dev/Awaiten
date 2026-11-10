using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     A <c>[RequestingType]</c> factory reached from an implementation a module builds receives the same type whether
///     the container sits in the module's own assembly, in a friend granted <c>InternalsVisibleTo</c> or in another
///     assembly, where the module's generated factory returns an accessible type in place of the implementation.
/// </summary>
public partial class ModuleRequestingTypeTests
{
	public enum Setup
	{
		SameAssembly,
		Friend,
		ImplementationAssembly,
		ReferenceAssembly,
		CompilationReference,
	}

	private static string Library(string types, string registrations, string members = "") => $$"""
		#nullable enable
		using System;
		using Awaiten;

		namespace Lib;

		public sealed class Log { public Log(string category) { } }
		{{types}}

		[Module]
		[Transient<Log>(Factory = nameof(CreateLog))]
		{{registrations}}
		public static partial class CoffeeModule
		{
		    public static Log CreateLog([RequestingType] Type? requestingType) => new(requestingType?.Name ?? "<root>");
		{{members}}
		}
		""";

	private static string Consumer(string registrations) => $$"""
		using Awaiten;
		using Lib;

		namespace App;

		[Container]
		[Import(typeof(CoffeeModule))]
		{{registrations}}
		public static partial class Shop;
		""";

	private static string Friend(string library)
		=> library.Replace("namespace Lib;", "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]\nnamespace Lib;");

	private static GeneratorResult Run(Setup setup, string library, string consumer)
		=> setup switch
		{
			Setup.SameAssembly => Generator.Run(library + "\n" + consumer.Replace("using Awaiten;", "").Replace("using Lib;", "").Replace("namespace App;", "")),
			Setup.Friend => Generator.RunWithGeneratedReferencedAssembly(Friend(library), consumer),
			Setup.ReferenceAssembly => Generator.RunWithGeneratedReferenceAssembly(library, consumer),
			Setup.CompilationReference => Generator.RunWithGeneratedCompilationReference(library, consumer),
			_ => Generator.RunWithGeneratedReferencedAssembly(library, consumer),
		};

	/// <summary>
	///     The type each construction in the container passes to the log factory: a <c>typeof</c> as written, or the type
	///     the generated method of the module that the container calls instead returns.
	/// </summary>
	private static (string[] Diagnostics, string[] Types) RequestingTypes(Setup setup, string library, string consumer)
	{
		GeneratorResult result = Run(setup, library, consumer);
		string container = result.Sources.Single(source => source.Key.EndsWith(".Shop.g.cs", StringComparison.Ordinal)).Value;
		string module = string.Concat(Generator.Run(setup == Setup.Friend ? Friend(library) : library).Sources.Values);
		return (result.Diagnostics, LogArgument().Matches(container)
			.Select(match => Evaluated(match.Groups[1].Value, module))
			.ToArray());
	}

	private static string Evaluated(string argument, string module)
	{
		if (argument.StartsWith("typeof(", StringComparison.Ordinal))
		{
			return argument["typeof(".Length..^1];
		}

		Match call = RequestingTypeCall().Match(argument);
		Match definition = Regex.Match(module, $@"global::System\.Type {call.Groups[1].Value}(?:<(.+?)>)?\(\)[^=]*=> typeof\((.+)\);");
		string type = definition.Groups[2].Value;
		if (call.Groups[2].Success)
		{
			type = Regex.Replace(type, $@"\b{definition.Groups[1].Value}\b", call.Groups[2].Value);
		}

		return type;
	}

	[GeneratedRegex(@"ResolveLog\(__s, (typeof\(.+?\)|[\w.:]+_RequestingType(?:<[^>]+>)?\(\))\)")]
	private static partial Regex LogArgument();

	[GeneratedRegex(@"ResolveLog\(__s, ([\w.:]+_RequestingType\(\))\)")]
	private static partial Regex RequestingTypeLogArgument();

	[GeneratedRegex(@"\.(\w+_RequestingType)(?:<(.+)>)?\(\)$")]
	private static partial Regex RequestingTypeCall();

	[GeneratedRegex(@"(?:[^\n]*///[^\n]*\n){4}[^\n]*_RequestingType\(\)[^\n]*\n\r?\n")]
	private static partial Regex RequestingTypeMethod();

	private static async Task AgreeEverywhere(string library, string consumer, params string[] expected)
	{
		foreach (Setup setup in Enum.GetValues<Setup>())
		{
			(string[] diagnostics, string[] types) = RequestingTypes(setup, library, consumer);
			await That(diagnostics).IsEmpty();
			await That(types).IsEqualTo(expected).InAnyOrder()
				.Because($"the {setup} container passes the type the module builds, as a container that constructs it itself does");
		}
	}

	[Test]
	public async Task InternalImplementation_IsTheRequestingTypeEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IKettle { }
			        internal sealed class Kettle : IKettle { public Kettle(Log log) { } }
			        """, "[Singleton<Kettle, IKettle>]"),
			Consumer(""),
			"global::Lib.Kettle");

	[Test]
	public async Task InternalDecorator_IsTheRequestingTypeEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IGrinder { }
			        internal sealed class Grinder : IGrinder { }
			        internal sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner, Log log) { } }
			        """, """
			             [Singleton<Grinder, IGrinder>]
			             [Decorate<LoggingGrinder, IGrinder>]
			             """),
			Consumer(""),
			"global::Lib.LoggingGrinder");

	[Test]
	public async Task InternalComposite_IsTheRequestingTypeEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IPart { }
			        internal sealed class Part : IPart { }
			        internal sealed class AllParts : IPart { public AllParts(System.Collections.Generic.IEnumerable<IPart> parts, Log log) { } }
			        """, """
			             [Transient<Part, IPart>]
			             [Composite<AllParts, IPart>]
			             """),
			Consumer(""),
			"global::Lib.AllParts");

	[Test]
	public async Task ScanMatch_IsTheRequestingTypeEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IPlugin { }
			        internal sealed class Roaster : IPlugin { public Roaster(Log log) { } }
			        """, "[Scan<IPlugin>(As = ScanAs.Marker)]"),
			Consumer(""),
			"global::Lib.Roaster");

	[Test]
	public async Task ScanMatchSharedThroughItsInstanceHandle_IsTheRequestingTypeEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IPlugin { }
			        public interface IRoaster { }
			        internal sealed class Roaster : IPlugin, IRoaster { public Roaster(Log log) { } }
			        """, "[Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]"),
			Consumer(""),
			"global::Lib.Roaster");

	[Test]
	public async Task InternalServiceBuiltIntoItsInstanceHandle_IsTheRequestingTypeEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IMill { }
			        internal sealed class Store { public Store(Log log) { } }
			        internal sealed class Mill : IMill { public Mill(Store store) { } }
			        """, """
			             [Singleton<Store>]
			             [Singleton<Mill, IMill>]
			             """),
			Consumer(""),
			"global::Lib.Store");

	[Test]
	public async Task InternalServiceOfAFactoryMember_IsTheRegisteredTypeEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IMill { }
			        internal sealed class Store { }
			        internal sealed class Mill : IMill { public Mill(Store store) { } }
			        """, """
			             [Singleton<Store>(Factory = nameof(MakeStore))]
			             [Singleton<Mill, IMill>]
			             """, "    internal static Store MakeStore(Log log) => new();"),
			Consumer(""),
			"global::Lib.Store");

	[Test]
	public async Task InjectedPropertyTheModuleAssigns_GetsTheImplementationEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IBrewer { }
			        internal sealed class Brewer : IBrewer { [Inject] public Log Log { get; set; } = null!; }
			        """, "[Transient<Brewer, IBrewer>]"),
			Consumer(""),
			"global::Lib.Brewer");

	[Test]
	public async Task OpenGenericInternalImplementation_IsTheClosedImplementationEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IRepo<T> { }
			        internal sealed class Repo<T> : IRepo<T> { public Repo(Log log) { } }
			        public sealed class Cafe { public Cafe(IRepo<int> repo) { } }
			        """, "[Transient(typeof(Repo<>), typeof(IRepo<>))]"),
			Consumer("[Transient<Cafe>]"),
			"global::Lib.Repo<int>");

	[Test]
	public async Task ImplementationPrivateToTheModule_IsTheRequestingTypeEverywhere()
		=> await AgreeEverywhere(
			Library("public interface ISecret { }", "[Singleton<CoffeeModule.Secret, ISecret>]",
				"    private sealed class Secret : ISecret { public Secret(Log log) { } }"),
			Consumer(""),
			"global::Lib.CoffeeModule.Secret");

	[Test]
	public async Task FactoryRegistration_IsTheReturnTypeOfTheFactoryEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IKettle { }
			        internal sealed class Kettle : IKettle { }
			        """, "[Singleton<IKettle>(Factory = nameof(MakeKettle))]", "    internal static IKettle MakeKettle(Log log) => new Kettle();"),
			Consumer(""),
			"global::Lib.IKettle");

	[Test]
	public async Task ModuleBuiltWithoutTheMethod_KeepsTheTypeItsFactoryReturns()
	{
		string library = Library("""
		                         public interface IKettle { }
		                         internal sealed class Kettle : IKettle { public Kettle(Log log) { } }
		                         """, "[Singleton<Kettle, IKettle>]");
		(Compilation generated, _) = Generator.RunGenerator(library, [], [], "ReferencedAssembly");
		SyntaxTree module = generated.SyntaxTrees.Single(tree => tree.FilePath.Contains("ModuleScan"));
		string older = RequestingTypeMethod().Replace(module.ToString(), "");
		Compilation olderLibrary = generated.ReplaceSyntaxTree(module, CSharpSyntaxTree.ParseText(older, (CSharpParseOptions)module.Options));

		(Compilation output, GeneratorDriverRunResult run) = Generator.RunGenerator(Consumer(""), [Generator.EmitToReference(olderLibrary),], []);
		string container = run.Results.SelectMany(result => result.GeneratedSources).Single(source => source.HintName.EndsWith(".Shop.g.cs", StringComparison.Ordinal)).SourceText.ToString();

		await That(older).DoesNotContain("_RequestingType");
		await That(output.GetDiagnostics()).IsEmpty();
		await That(container).Contains("Registration_Kettle_2655994a(ResolveLog(__s, typeof(global::Lib.IKettle)))")
			.Because("a module built by an older generator carries no method for the implementation, so the container passes the type the factory returns, as before");
	}

	[Test]
	public async Task ModuleFactory_ReturnsTheImplementationItBuilds()
	{
		GeneratorResult result = Generator.Run(Library("""
		                                               public interface IKettle { }
		                                               internal sealed class Kettle : IKettle { public Kettle(Log log) { } }
		                                               """, "[Singleton<Kettle, IKettle>]"));
		string module = result.Sources.Single(source => source.Key.Contains("ModuleScan")).Value;

		await That(module).Contains("public static global::System.Type Registration_Kettle_2655994a_RequestingType() => typeof(global::Lib.Kettle);")
			.Because("a container in another assembly calls the method for the type it cannot name, next to the factory of the same accessibility");
	}

	[Test]
	public async Task OpenGenericClosedOverATypeInAnInternalsNamespace_IsTheClosedImplementationEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public static class Internals { public sealed class Bean { } }
			        public interface IRepo<T> { }
			        internal sealed class Repo<T> : IRepo<T> { public Repo(Log log) { } }
			        public sealed class Cafe { public Cafe(IRepo<Internals.Bean> repo) { } }
			        """, "[Transient(typeof(Repo<>), typeof(IRepo<>))]"),
			Consumer("[Transient<Cafe>]"),
			"global::Lib.Repo<global::Lib.Internals.Bean>");

	[Test]
	public async Task FactoryRegistrationWithAHook_IsTheRegisteredTypeEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IKettle { }
			        internal sealed class Kettle : IKettle { }
			        """, "[Singleton<IKettle>(Factory = nameof(MakeKettle), OnActivated = nameof(Warm))]",
				"    internal static IKettle MakeKettle(Log log) => new Kettle();\n    internal static void Warm(IKettle kettle, Log log) { }"),
			Consumer(""),
			"global::Lib.IKettle", "global::Lib.IKettle");

	[Test]
	public async Task KeyedInternalDecorator_IsTheRequestingTypeEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IGrinder { }
			        internal sealed class Grinder : IGrinder { }
			        internal sealed class LoggingGrinder : IGrinder { public LoggingGrinder(IGrinder inner, Log log) { } }
			        public sealed class Cafe { public Cafe([FromKey("a")] IGrinder g) { } }
			        """, """
			             [Singleton<Grinder, IGrinder>(Key = "a")]
			             [Decorate<LoggingGrinder, IGrinder>(AllKeys = true)]
			             [Transient<Cafe>]
			             """),
			Consumer(""),
			"global::Lib.LoggingGrinder");

	[Test]
	public async Task OpenGenericInternalDecorator_IsTheClosedDecoratorEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IRepo<T> { }
			        internal sealed class Repo<T> : IRepo<T> { public Repo(Log log) { } }
			        internal sealed class LoggingRepo<T> : IRepo<T> { public LoggingRepo(IRepo<T> inner, Log log) { } }
			        public sealed class Cafe { public Cafe(IRepo<int> repo) { } }
			        """, "[Transient(typeof(Repo<>), typeof(IRepo<>))] [Decorate(typeof(LoggingRepo<>), typeof(IRepo<>))]"),
			Consumer("[Transient<Cafe>]"),
			"global::Lib.Repo<int>", "global::Lib.LoggingRepo<int>");

	[Test]
	public async Task DeferredPropertyAndHookOfAnInternalImplementation_GetTheImplementationEverywhere()
		=> await AgreeEverywhere(
			Library("""
			        public interface IBrewer { }
			        internal sealed class Brewer : IBrewer { public Brewer(Log log) { } [Inject(Deferred = true)] public Log Late { get; set; } = null!; }
			        """, "[Singleton<Brewer, IBrewer>(OnActivated = nameof(Warm))]", "    internal static void Warm(Brewer b, Log log) { }"),
			Consumer(""),
			"global::Lib.Brewer", "global::Lib.Brewer", "global::Lib.Brewer");

	[Test]
	public async Task ImplementationPrivateToABaseContainer_IsTheRequestingTypeOfAnExtendingContainer()
	{
		const string library = """
		                       #nullable enable
		                       using System;
		                       using Awaiten;

		                       [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TestAssembly")]

		                       namespace Lib;

		                       public sealed class Log { public Log(string category) { } }
		                       public interface ISecret { }

		                       [Container]
		                       [Transient<Log>(Factory = nameof(CreateLog))]
		                       [Singleton<Shop.Secret, ISecret>]
		                       public static partial class Shop
		                       {
		                           public static Log CreateLog([RequestingType] Type? requestingType) => new(requestingType?.Name ?? "<root>");
		                           private sealed class Secret : ISecret { public Secret(Log log) { } }
		                       }
		                       """;
		GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(library, """
		                                                                              using Awaiten;
		                                                                              using Lib;

		                                                                              namespace App;

		                                                                              [Container(Extends = typeof(Shop))]
		                                                                              public static partial class TestShop;
		                                                                              """);
		string container = result.Sources.Single(source => source.Key.EndsWith(".TestShop.g.cs", StringComparison.Ordinal)).Value;
		string own = string.Concat(Generator.Run(library).Sources.Values);

		await That(result.Diagnostics).IsEmpty();
		await That(own).Contains("ResolveLog(__s, typeof(global::Lib.Shop.Secret))");
		await That(RequestingTypeLogArgument().Matches(container).Select(match => Evaluated(match.Groups[1].Value, own)))
			.IsEqualTo(["global::Lib.Shop.Secret",])
			.Because("the extending container builds the base's private implementation through the base's factory and passes the type the base passes itself");
	}
}
