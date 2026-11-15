using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Emit;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	/// <summary>
	///     Two libraries declare a public type of the same full name, which plain C# cannot name in a project that references
	///     both, so a container there that would name it reports it instead of generating code that does not compile.
	/// </summary>
	public class Awt260AmbiguousTypeName
	{
		public enum Setup
		{
			ImplementationAssembly,
			ReferenceAssembly,
			CompilationReference,
			Friend,
		}

		private const string Friend = "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]";

		private const string ImportBoth = """
			using Awaiten;

			namespace MyCode;

			[Container]
			[Import(typeof(First.ClockModule))]
			[Import(typeof(Second.ClockModule))]
			public static partial class MyContainer;
			""";

		private const string ImportFirst = """
			using Awaiten;

			namespace MyCode;

			[Container]
			[Import(typeof(First.ClockModule))]
			public static partial class MyContainer;
			""";

		private static string Library(
			string name,
			string clock = "public sealed class Clock { }",
			string registrations = "[Singleton<Lib.Clock>]",
			string types = "") => $$"""
			using Awaiten;

			namespace Lib
			{
			    {{clock}}
			}

			namespace {{name}}
			{
			    {{types}}

			    [Module]
			    {{registrations}}
			    public static partial class ClockModule { }
			}
			""";

		private static string Expected(string module, string verb = "registers", string type = "Lib.Clock", string line = "6", string column = "2", string declared = "Lib.Clock")
			=> $"({line},{column}): error AWT260: The generated container cannot name '{type}', which '{module}' {verb}, because the assemblies 'FirstAssembly' and 'SecondAssembly' each declare a type or namespace named '{declared}'; keep the container in a project that references only one of them, or rename one of them";

		private static GeneratorResult Run(Setup setup, string first, string second, string consumer)
		{
			string grants = setup == Setup.Friend ? $"using Awaiten;\n{Friend}" : "using Awaiten;";
			(Compilation firstCompilation, _) = Generator.RunGenerator(first.Replace("using Awaiten;", grants), [], [], "FirstAssembly");
			(Compilation secondCompilation, _) = Generator.RunGenerator(second.Replace("using Awaiten;", grants), [], [], "SecondAssembly");
			return Generator.RunWithReferences(consumer, [Reference(firstCompilation), Reference(secondCompilation),]);

			MetadataReference Reference(Compilation compilation)
				=> setup switch
				{
					Setup.ReferenceAssembly => Generator.EmitToReference(compilation, new EmitOptions(metadataOnly: true, includePrivateMembers: false)),
					Setup.CompilationReference => Generator.ToCompilationReference(compilation),
					_ => Generator.EmitToReference(compilation),
				};
		}

		[Test]
		[Arguments(Setup.ImplementationAssembly)]
		[Arguments(Setup.ReferenceAssembly)]
		[Arguments(Setup.CompilationReference)]
		[Arguments(Setup.Friend)]
		public async Task ARegistrationOfEachSameNamedType_ReportsEachAtItsImport(Setup setup)
		{
			GeneratorResult result = Run(setup, Library("First"), Library("Second"), ImportBoth);

			await That(result.Diagnostics).HasCount(2);
			await That(result.Diagnostics).Contains(Expected("First.ClockModule"))
				.And.Contains(Expected("Second.ClockModule", line: "7"))
				.Because("'global::Lib.Clock' is ambiguous between the two references, so the container reports each type instead of emitting CS0433");
		}

		[Test]
		[Arguments(Setup.ImplementationAssembly)]
		[Arguments(Setup.ReferenceAssembly)]
		[Arguments(Setup.CompilationReference)]
		[Arguments(Setup.Friend)]
		public async Task AnInstanceHandleOfEachSameNamedType_ReportsEachAtItsImport(Setup setup)
		{
			static string Handled(string name) => Library(name, clock: "public sealed class Clock { internal Clock() { } }");

			GeneratorResult result = Run(setup, Handled("First"), Handled("Second"), ImportBoth);

			await That(result.Diagnostics).HasCount(2);
			await That(result.Diagnostics).Contains(Expected("First.ClockModule"))
				.And.Contains(Expected("Second.ClockModule", line: "7"))
				.Because("the container holds each clock in a field of its type, whether it builds it itself or through the module's handle");
		}

		[Test]
		[Arguments(Setup.ImplementationAssembly)]
		[Arguments(Setup.ReferenceAssembly)]
		public async Task ATypeArgument_IsReportedWithTheTypeItIsPartOf(Setup setup)
		{
			string first = Library(
				"First",
				registrations: "[Singleton<Holder<Lib.Clock>, IHolder<Lib.Clock>>]",
				types: "public interface IHolder<T> { } public sealed class Holder<T> : IHolder<T> { }");

			GeneratorResult result = Run(setup, first, Library("Second", registrations: ""), ImportFirst);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.ClockModule", type: "First.IHolder<Lib.Clock>"))
				.Because("the second library declares 'Lib.Clock' even though the container imports none of its modules, and the implementation is reported with the service it shares the clock with");
		}

		[Test]
		[Arguments(Setup.ImplementationAssembly)]
		[Arguments(Setup.ReferenceAssembly)]
		public async Task ADependencyImportedFromTheHost_IsReportedForTheTypeThatTakesIt(Setup setup)
		{
			string first = Library(
				"First",
				registrations: "[Singleton<Service>]",
				types: "public sealed class Service { public Service(Lib.Clock clock) { } }");

			GeneratorResult result = Run(setup, first, Library("Second", registrations: ""), ImportFirst.Replace("[Container]", "[Container]\n[ImportServices]"));

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.Service", verb: "takes", line: "7"))
				.Because("the container asks its host for the clock by naming its type");
		}

		[Test]
		public async Task AnEmptyCollection_IsReportedForTheTypeThatTakesIt()
		{
			string first = Library(
				"First",
				registrations: "[Singleton<Service>]",
				types: "public sealed class Service { public Service(System.Collections.Generic.IEnumerable<Lib.Clock> clocks) { } }");

			GeneratorResult result = Run(Setup.ImplementationAssembly, first, Library("Second", registrations: ""), ImportFirst);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.Service", verb: "takes", type: "System.Collections.Generic.IEnumerable<Lib.Clock>"))
				.Because("the container builds the empty collection as an array of the clock's type");
		}

		[Test]
		[Arguments("public enum Clock { Fast }", "Lib.Clock.Fast", "Lib.Clock")]
		[Arguments("public sealed class Clock { }", "typeof(Lib.Clock)", "Lib.Clock")]
		[Arguments("public sealed class Clock { }", "typeof(System.Collections.Generic.List<Lib.Clock>)", "System.Collections.Generic.List<Lib.Clock>")]
		public async Task ARegistrationKey_IsReportedForTheModuleThatUsesIt(string clock, string key, string type)
		{
			string first = Library("First", clock, $"[Singleton<Service>(Key = {key})]", "public sealed class Service { }");

			GeneratorResult result = Run(Setup.ImplementationAssembly, first, Library("Second", clock, registrations: ""), ImportFirst);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.ClockModule", verb: "uses as a key", type: type))
				.Because("the container's keyed dispatch spells the key, and so its type, in the generated code");
		}

		[Test]
		[Arguments("public enum Clock { Fast }", "Lib.Clock.Fast")]
		[Arguments("public sealed class Clock { }", "typeof(Lib.Clock)")]
		public async Task AKeyOfADependencyImportedFromTheHost_IsReportedForTheTypeThatTakesIt(string clock, string key)
		{
			string first = Library(
				"First",
				clock,
				"[Singleton<Consumer>]",
				$"public sealed class Service {{ }} public sealed class Consumer {{ public Consumer([FromKey({key})] Service service) {{ }} }}");

			GeneratorResult result = Run(Setup.ImplementationAssembly, first, Library("Second", clock, registrations: ""), ImportFirst.Replace("[Container]", "[Container]\n[ImportService<First.Service>]"));

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.Consumer", verb: "uses as a key", line: "7"))
				.Because("the container forwards the key to its host when it asks for the service");
		}

		[Test]
		public async Task AKeyOfAnOpenGenericRegistration_IsReportedForTheModuleThatUsesIt()
		{
			string first = Library(
				"First",
				"public enum Clock { Fast }",
				"[Singleton(typeof(Holder<>), typeof(IHolder<>), Key = Lib.Clock.Fast)]",
				"public interface IHolder<T> { } public sealed class Holder<T> : IHolder<T> { } public sealed class Service { public Service([FromKey(Lib.Clock.Fast)] IHolder<string> holder) { } } [Module] [Singleton<Service>] public static partial class ServiceModule { }");

			GeneratorResult result = Run(Setup.ImplementationAssembly, first, Library("Second", "public enum Clock { Fast }", registrations: ""), ImportFirst.Replace("[Import(typeof(First.ClockModule))]", "[Import(typeof(First.ClockModule))]\n[Import(typeof(First.ServiceModule))]"));

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.ClockModule", verb: "uses as a key"))
				.Because("each closing of the open registration keeps its key, which the keyed dispatch spells");
		}

		[Test]
		public async Task AKeyOfAPropertyEntryOfAnExtendedContainer_IsReportedForTheTypeWhosePropertyItIs()
		{
			string first = Library(
				"First",
				"public enum Clock { Fast }",
				registrations: "",
				types: "public sealed class Service { } public sealed class Consumer { public Service? Service { get; set; } } [Container] [ImportService<Service>] [Singleton<Consumer>] [InjectProperty<Consumer>(nameof(Consumer.Service), Key = Lib.Clock.Fast)] public static partial class BaseShop;");
			const string consumer = """
				using Awaiten;

				namespace MyCode;

				[Container(Extends = typeof(First.BaseShop))]
				public static partial class MyContainer;
				""";

			GeneratorResult result = Run(Setup.ImplementationAssembly, first, Library("Second", "public enum Clock { Fast }", registrations: ""), consumer);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.Consumer", verb: "uses as a key", line: "5", column: "12"))
				.Because("the container forwards the entry's key to its host when it asks for the property's service");
		}

		[Test]
		[Arguments(
			"public sealed class Clock : First.IService { public Clock(First.IService inner) { } }",
			"[Singleton<Service, IService>] [Decorate<Lib.Clock, IService>]",
			"public interface IService { } public sealed class Service : IService { }",
			"Lib.Clock", "First.ClockModule", "registers")]
		[Arguments(
			"public sealed class Clock : First.IService { public Clock(System.Collections.Generic.IEnumerable<First.IService> services) { } }",
			"[Singleton<Service, IService>] [Composite<Lib.Clock, IService>]",
			"public interface IService { } public sealed class Service : IService { }",
			"Lib.Clock", "First.ClockModule", "registers")]
		[Arguments(
			"public sealed class Clock { }",
			"[Singleton(typeof(Holder<>), typeof(IHolder<>))] [Singleton<Service>]",
			"public interface IHolder<T> { } public sealed class Holder<T> : IHolder<T> { } public sealed class Service { public Service(IHolder<Lib.Clock> holder) { } }",
			"First.IHolder<Lib.Clock>", "First.ClockModule", "registers")]
		[Arguments(
			"public sealed class Clock { }",
			"[Transient<Service>]",
			"public sealed class Service { public Service([Arg] Lib.Clock clock) { } }",
			"Lib.Clock", "First.Service", "takes")]
		[Arguments(
			"public sealed class Clock { }",
			"[Singleton<Service>(OnActivated = nameof(Activate))]",
			"public sealed class Service { } public static partial class ClockModule { internal static void Activate(Service service, Lib.Clock clock) { } }",
			"Lib.Clock", "First.ClockModule", "takes")]
		[Arguments(
			"public sealed class Clock { }",
			"[AsyncInitializer<IWarmUp>(nameof(IWarmUp.WarmUpAsync))] [Singleton<Service>]",
			"public interface IWarmUp { System.Threading.Tasks.Task WarmUpAsync(Lib.Clock clock); } public sealed class Service : IWarmUp { public System.Threading.Tasks.Task WarmUpAsync(Lib.Clock clock) => System.Threading.Tasks.Task.CompletedTask; }",
			"Lib.Clock", "First.IWarmUp", "takes")]
		[Arguments(
			"public sealed class Clock { }",
			"[Singleton<Service, IService>] [Decorate<Decorator, IService>(When = nameof(Audit))]",
			"public interface IService { } public sealed class Service : IService { } public sealed class Decorator : IService { public Decorator(IService inner) { } } public static partial class ClockModule { internal static bool Audit(Lib.Clock clock) => true; }",
			"Lib.Clock", "First.ClockModule", "takes")]
		[Arguments(
			"public sealed class Clock { }",
			"[Singleton<Service>]",
			"public sealed class Service { internal Service() { } [Inject(Deferred = true)] public System.Collections.Generic.IEnumerable<Lib.Clock> Clock { get; set; } = null!; }",
			"System.Collections.Generic.IEnumerable<Lib.Clock>", "First.Service", "takes")]
		public async Task AFeatureThatNamesTheType_ReportsIt(string clock, string registrations, string types, string type, string needer, string verb)
		{
			string first = Library("First", clock, registrations, types);

			GeneratorResult result = Run(Setup.ImplementationAssembly, first, Library("Second", registrations: ""), ImportFirst.Replace("[Container]", "[Container]\n[ImportServices]"));

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected(needer, verb: verb, type: type, line: "7"))
				.Because("decorators, composites, open generic closings, runtime arguments, hooks, async initializers, decorator predicates and deferred setters each make the container spell the type, here of a clock it asks its host for");
		}

		[Test]
		public async Task AnExtendedContainerOfAnotherAssembly_IsReportedForTheBaseContainer()
		{
			string first = Library("First", registrations: "", types: "[Container] [Singleton<Lib.Clock>] public static partial class BaseShop;");
			const string consumer = """
				using Awaiten;

				namespace MyCode;

				[Container(Extends = typeof(First.BaseShop))]
				public static partial class MyContainer;
				""";

			GeneratorResult result = Run(Setup.ImplementationAssembly, first, Library("Second", registrations: ""), consumer);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.BaseShop", line: "5", column: "12"))
				.Because("the extending container builds the base container's registrations itself");
		}

		[Test]
		public async Task AServiceAModuleImportsFromTheHost_IsReportedForTheModule()
		{
			string first = Library("First", registrations: "[ImportService<Lib.Clock>]");

			GeneratorResult result = Run(Setup.ImplementationAssembly, first, Library("Second", registrations: ""), ImportFirst);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.ClockModule", verb: "imports"))
				.Because("the container's by-type dispatch asks the host for the imported clock by its type");
		}

		[Test]
		public async Task TheTypeOfAnAsyncInitializer_IsReportedForTheModuleThatDeclaresIt()
		{
			string first = Library(
				"First",
				"public interface IWarmUp { System.Threading.Tasks.Task WarmUpAsync(); }",
				"[AsyncInitializer<Lib.IWarmUp>(nameof(Lib.IWarmUp.WarmUpAsync))] [Singleton<Service>]",
				"public sealed class Service : Lib.IWarmUp { public System.Threading.Tasks.Task WarmUpAsync() => System.Threading.Tasks.Task.CompletedTask; }");

			GeneratorResult result = Run(Setup.ImplementationAssembly, first, Library("Second", "public interface IWarmUp { }", registrations: ""), ImportFirst);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.ClockModule", verb: "declares as an async initializer", type: "Lib.IWarmUp", declared: "Lib.IWarmUp"))
				.Because("the container casts the instance to the initializer's type to run it");
		}

		private const string HandledModule = "[Module] [Singleton<First.Kettle>] public static partial class Mod { }";

		private const string Kettle = "public sealed class Kettle { internal Kettle() { } }";

		private const string Extending = """
			using Awaiten;

			namespace MyCode;

			[Container(Extends = typeof(First.BaseShop))]
			public static partial class MyContainer;
			""";

		private static string BaseShop(string kettle, string module = HandledModule)
			=> Library("First", module, registrations: "", types: $"{kettle} [Container] [Import(typeof(Lib.Mod))] public static partial class BaseShop;");

		[Test]
		[Arguments(Kettle, HandledModule)]
		[Arguments("public sealed class Kettle { }", "[Module] [Singleton<First.Kettle>(OnActivated = nameof(Activate))] public static partial class Mod { public static void Activate(First.Kettle kettle) { } }")]
		[Arguments("public sealed class Kettle { }", "[Module] [Singleton<First.Kettle>(Factory = nameof(Create))] public static partial class Mod { public static First.Kettle Create() => new(); }")]
		[Arguments("public sealed class Kettle { }", "[Module] [Singleton<First.Kettle>(EagerWhen = nameof(Eager))] public static partial class Mod { public static bool Eager() => true; }")]
		public async Task AModuleOfAnExtendedContainer_IsReportedForTheBaseContainerThatImportsIt(string kettle, string module)
		{
			GeneratorResult result = Run(Setup.ImplementationAssembly, BaseShop(kettle, module), Library("Second", "public static class Mod { }", registrations: ""), Extending);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.BaseShop", verb: "imports", type: "Lib.Mod", line: "5", column: "12", declared: "Lib.Mod"))
				.Because("a handle, a hook, a factory or a predicate the module declares makes the container name the module's type");
		}

		[Test]
		public async Task AModuleTheContainerNeverNames_IsNotReported()
		{
			GeneratorResult result = Run(Setup.ImplementationAssembly, BaseShop("public sealed class Kettle { }"), Library("Second", "public static class Mod { }", registrations: ""), Extending);

			await That(result.Diagnostics).IsEmpty()
				.Because("the container constructs the public kettle itself, so its generated code never names the module");
		}

		[Test]
		public async Task AGenericTypeThatSharesTheNameOfAModuleTheContainerNeverNames_IsNotReported()
		{
			const string module = "[Module] [Singleton<First.Kettle>] [Singleton<Lib.Mod<int>>] public static partial class Mod { } public sealed class Mod<T> { }";

			GeneratorResult result = Run(Setup.ImplementationAssembly, BaseShop("public sealed class Kettle { }", module), Library("Second", "public static class Mod { }", registrations: ""), Extending);

			await That(result.Diagnostics).IsEmpty()
				.Because("'global::Lib.Mod<int>' names the generic type of another arity, and the container never calls into the module 'Lib.Mod'");
		}

		[Test]
		public async Task AModuleOfAnExtendedContainerOfTheSameAssembly_IsReportedOnceForTheBaseContainer()
		{
			(Compilation first, _) = Generator.RunGenerator(Library("First", HandledModule, registrations: "", types: Kettle), [], [], "FirstAssembly");
			(Compilation second, _) = Generator.RunGenerator(Library("Second", "public static class Mod { }", registrations: ""), [], [], "SecondAssembly");
			const string consumer = """
				extern alias Other;
				using Awaiten;

				namespace MyCode;

				[Container]
				[Import(typeof(Other::Lib.Mod))]
				public static partial class BaseShop;

				[Container(Extends = typeof(BaseShop))]
				public static partial class MyContainer;
				""";

			GeneratorResult result = Generator.RunWithReferences(consumer, [Generator.EmitToReference(first).WithAliases(["Other",]), Generator.EmitToReference(second),]);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("MyCode.BaseShop", verb: "imports", type: "Lib.Mod", line: "7", declared: "Lib.Mod"))
				.Because("the base container reports the module it imports itself, so the extending container does not report it again");
		}

		[Test]
		public async Task AModuleTheContainerImportsThroughAnAlias_IsReportedForTheContainer()
		{
			(Compilation first, _) = Generator.RunGenerator(Library("First", HandledModule, registrations: "", types: Kettle), [], [], "FirstAssembly");
			(Compilation second, _) = Generator.RunGenerator(Library("Second", "public static class Mod { }", registrations: ""), [], [], "SecondAssembly");
			const string consumer = """
				extern alias Other;
				using Awaiten;

				namespace MyCode;

				[Container]
				[Import(typeof(Other::Lib.Mod))]
				public static partial class MyContainer;
				""";

			GeneratorResult result = Generator.RunWithReferences(consumer, [Generator.EmitToReference(first).WithAliases(["Other",]), Generator.EmitToReference(second),]);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("MyCode.MyContainer", verb: "imports", type: "Lib.Mod", line: "7", declared: "Lib.Mod"))
				.Because("'global::Lib.Mod' names the second library's type, so the container cannot name the module it imports through the alias");
		}

		[Test]
		public async Task ASameNamedTypeOfAnAliasedReference_IsNotReported()
		{
			(Compilation first, _) = Generator.RunGenerator(Library("First"), [], [], "FirstAssembly");
			(Compilation second, _) = Generator.RunGenerator(Library("Second", registrations: ""), [], [], "SecondAssembly");

			GeneratorResult result = Generator.RunWithReferences(ImportFirst, [Generator.EmitToReference(first), Generator.EmitToReference(second).WithAliases(["Other",]),]);

			await That(result.Diagnostics).IsEmpty()
				.Because("the second library's clock is reachable only through its alias, so 'global::Lib.Clock' names only the first one");
		}

		[Test]
		public async Task ADefaultTheContainerReplaces_IsNotReported()
		{
			string first = Library(
				"First",
				registrations: "[Singleton<Lib.Clock, IClock>(Fallback = Fallback.Silent)]",
				clock: "public sealed class Clock : First.IClock { }",
				types: "public interface IClock { }");
			string consumer = ImportFirst.Replace("[Container]", "[Container]\n[Singleton<MyClock, First.IClock>]") + "\npublic sealed class MyClock : First.IClock { }";

			GeneratorResult result = Run(Setup.ImplementationAssembly, first, Library("Second", registrations: ""), consumer);

			await That(result.Diagnostics).IsEmpty()
				.Because("the container's own registration replaces the module's default, so its clock never joins the graph");
		}

		[Test]
		public async Task ASameNamedTypeTheContainerNeverNames_IsNotReported()
		{
			string first = Library("First", registrations: "[Singleton<Kettle>]", types: "public sealed class Kettle { }");

			GeneratorResult result = Run(Setup.ImplementationAssembly, first, Library("Second", registrations: ""), ImportFirst);

			await That(result.Diagnostics).IsEmpty()
				.Because("both libraries declare 'Lib.Clock', but nothing in the container's graph needs it");
		}

		[Test]
		[Arguments("public")]
		[Arguments("internal")]
		public async Task ANamespaceOfTheSameFullName_IsReported(string accessibility)
		{
			GeneratorResult result = Run(
				Setup.ImplementationAssembly,
				Library("First"),
				Library("Second", clock: $"namespace Clock {{ {accessibility} sealed class Tick {{ }} }}", registrations: ""),
				ImportFirst);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.ClockModule"))
				.Because("'global::Lib.Clock' conflicts with the namespace 'Lib.Clock' of the second library (CS0434), whatever the accessibility of the types in it");
		}

		[Test]
		public async Task ATypeNamedLikeANamespaceOfTheType_IsReported()
		{
			GeneratorResult result = Run(Setup.ImplementationAssembly, Library("First"), "public class Lib { }", ImportFirst);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.ClockModule", declared: "Lib"))
				.Because("'global::Lib' conflicts with the type 'Lib' of the second library (CS0434), so 'global::Lib.Clock' cannot name the clock");
		}

		[Test]
		public async Task ATypeNamedLikeANamespaceOfTheTypeInternalToTheOtherAssembly_IsNotReported()
		{
			GeneratorResult result = Run(Setup.ImplementationAssembly, Library("First"), "internal class Lib { }", ImportFirst);

			await That(result.Diagnostics).IsEmpty()
				.Because("the container cannot see the second library's internal type 'Lib', so 'global::Lib' names only the namespace");
		}

		[Test]
		public async Task ASameNamedTypeInternalToTheOtherAssembly_IsNotReported()
		{
			GeneratorResult result = Run(
				Setup.ImplementationAssembly,
				Library("First"),
				Library("Second", clock: "internal sealed class Clock { }", registrations: ""),
				ImportFirst);

			await That(result.Diagnostics).IsEmpty()
				.Because("the container cannot see the second library's internal clock, so 'global::Lib.Clock' names only the first one");
		}

		private static GeneratorResult RunInFirstAssembly(string imports)
		{
			(Compilation second, _) = Generator.RunGenerator(Library("Second"), [], [], "SecondAssembly");
			string first = Library("First").Replace("public static partial class ClockModule { }", $$"""
				public static partial class ClockModule { }

				    [Container]
				    {{imports}}
				    public static partial class MyContainer;
				""");
			return Generator.RunWithReferences(first, [Generator.EmitToReference(second),], "FirstAssembly");
		}

		[Test]
		public async Task AContainerInTheAssemblyOfOneType_ReportsOnlyTheOtherOne()
		{
			GeneratorResult result = RunInFirstAssembly("[Import(typeof(ClockModule))]\n[Import(typeof(Second.ClockModule))]");

			await That(result.Diagnostics.Where(diagnostic => !diagnostic.Contains("warning CS0436"))).HasSingle().Which.IsEqualTo(
				"(18,2): error AWT260: The generated container cannot name 'Lib.Clock', which 'Second.ClockModule' registers, because the assemblies 'FirstAssembly' and 'SecondAssembly' each declare a type or namespace named 'Lib.Clock'; keep the container in a project that references only one of them, or rename one of them")
				.Because("'global::Lib.Clock' names the clock of the container's own assembly, as in the module's own attribute, so only the other library's clock cannot be named");
		}

		[Test]
		public async Task AContainerInTheAssemblyOfOneType_NamesItsOwnType()
		{
			GeneratorResult result = RunInFirstAssembly("[Import(typeof(ClockModule))]");

			await That(result.Diagnostics).IsNotEmpty().And.All().Satisfy(diagnostic => diagnostic.Contains("warning CS0436"))
				.Because("'global::Lib.Clock' names the clock of the container's own assembly, with the warning the module's own attribute gets too");
		}
	}
}
