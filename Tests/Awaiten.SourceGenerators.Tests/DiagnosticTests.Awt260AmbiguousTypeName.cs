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

		private static string Expected(string module, string verb = "registers", string type = "Lib.Clock", string line = "6")
			=> $"({line},2): error AWT260: The generated container cannot name '{type}', which '{module}' {verb}, because the assemblies 'FirstAssembly' and 'SecondAssembly' each declare a type named 'Lib.Clock'; keep the container in a project that references only one of them, or rename one of the types";

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
		public async Task AServiceAModuleImportsFromTheHost_IsReportedForTheModule()
		{
			string first = Library("First", registrations: "[ImportService<Lib.Clock>]");

			GeneratorResult result = Run(Setup.ImplementationAssembly, first, Library("Second", registrations: ""), ImportFirst);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected("First.ClockModule", verb: "imports"))
				.Because("the container's by-type dispatch asks the host for the imported clock by its type");
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
				"(18,2): error AWT260: The generated container cannot name 'Lib.Clock', which 'Second.ClockModule' registers, because the assemblies 'FirstAssembly' and 'SecondAssembly' each declare a type named 'Lib.Clock'; keep the container in a project that references only one of them, or rename one of the types")
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
