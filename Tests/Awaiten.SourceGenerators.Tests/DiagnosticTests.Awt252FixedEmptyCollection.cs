using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	/// <summary>
	///     A decorator in a library that grants <c>InternalsVisibleTo</c> to a second library takes a collection of an
	///     internal element type that only the second library registers: the first library's module passes it empty,
	///     while a container that builds the decorator itself passes the second library's registration.
	/// </summary>
	public partial class Awt252FixedEmptyCollection
	{
		private const string Friend = "[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"TestAssembly\")]";

		private const string App = """
			using Awaiten;

			namespace MyCode;

			[Container]
			[Import(typeof(Lib.BrewModule))]
			[Import(typeof(Lib2.ThingModule))]
			public static partial class MyContainer;
			""";

		private const string Expected =
			"(6,2): error AWT252: 'Lib.BrewModule' passes 'Lib.Counting' an empty collection of 'Lib.IThing', fixed in the module's own build since no module of its assembly registers it, although 'Lib2.ThingModule' registers it in this container; make 'Lib.IThing' public so the module's factory takes the collection from the container, or grant this assembly InternalsVisibleTo from the assemblies of 'Lib.BrewModule' and 'Lib2.ThingModule' so the container builds 'Lib.Counting' itself";

		private const string InternalThing = "internal sealed class Thing : Lib.IThing { }";

		private static string Library1(string grants = "", string parameter = "IEnumerable<IThing> things") => $$"""
			using System.Collections.Generic;
			using Awaiten;

			[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Library2")]
			{{grants}}

			namespace Lib;

			public interface IBrew { }
			internal interface IThing { }
			internal sealed class Brew : IBrew { }
			internal sealed class Counting : IBrew { public Counting(IBrew inner, {{parameter}}) { } }

			[Module]
			[Singleton<Brew, IBrew>]
			[Decorate<Counting, IBrew>]
			public static partial class BrewModule { }
			""";

		private static string Library2(string grants = "", string registration = "[Singleton<Thing, Lib.IThing>]", string thing = InternalThing) => $$"""
			using Awaiten;

			{{grants}}

			namespace Lib2;

			{{thing}}

			[Module]
			{{registration}}
			public static partial class ThingModule { }
			""";

		private static string Container(GeneratorResult result) => result.Sources["Awaiten.MyCode.MyContainer.g.cs"];

		[GeneratedRegex("public const string EmptyCollection_IThing_[0-9a-f]{8} = \"Lib\\.Counting\\\\nLib\\.IThing\";")]
		private static partial Regex Record();

		[Test]
		public async Task TheModuleRecordsTheElementTypeItPassesEmpty()
		{
			GeneratorResult library = Generator.Run(Library1());

			await That(library.Diagnostics).IsEmpty();
			await That(Record().IsMatch(library.Sources["Awaiten.ModuleScan.Lib.BrewModule.g.cs"])).IsTrue()
				.Because("a consumer finds the element type by its documentation id and reports the type that receives the empty collection");
		}

		[Test]
		public async Task AnElementOfAPublicGenericTypeOverAnInternalOne_IsReported()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[
					Library1(parameter: "IEnumerable<System.IObserver<IThing>> things"),
					Library2(registration: "[Singleton<Observer, System.IObserver<Lib.IThing>>]", thing: "internal sealed class Observer : System.IObserver<Lib.IThing> { public void OnCompleted() { } public void OnError(System.Exception error) { } public void OnNext(Lib.IThing value) { } }"),
				],
				App);

			await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT252") && diagnostic.Contains("an empty collection of 'System.IObserver<Lib.IThing>'"))
				.Because("the element type is not public for its type argument, whose assembly grants InternalsVisibleTo, although the assembly of the generic type grants none");
		}

		[Test]
		public async Task AVarianceMatch_IsReported()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[
					Library1(parameter: "IEnumerable<IHandler<Order>> handlers")
						.Replace("internal interface IThing { }", "internal interface IHandler<in T> { } internal class Message { } internal sealed class Order : Message { }"),
					Library2(registration: "[Singleton<Handler, Lib.IHandler<Lib.Message>>]", thing: "internal sealed class Handler : Lib.IHandler<Lib.Message> { }"),
				],
				App);

			await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT252") && diagnostic.Contains("an empty collection of 'Lib.IHandler<Lib.Order>'"))
				.Because("a collection of 'IHandler<Order>' in the module's assembly holds the contravariant registration of 'IHandler<Message>'");
		}

		[Test]
		public async Task AWhenInjectedIntoRegistration_IsNotReported()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[Library1(), Library2(registration: "[Singleton<Thing, Lib.IThing>(WhenInjectedInto = typeof(Lib.Counting))]"),],
				App);

			await That(result.Diagnostics).DoesNotContain("*AWT252*").AsWildcard()
				.Because("a contextual registration is no member of a collection, so the collection is empty in the module's assembly too");
		}

		[Test]
		public async Task AnElementTheContainerRegistersItself_IsReported()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[
					"""
					[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Library2")]
					[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TestAssembly")]

					namespace Lib0;

					internal interface IThing { }
					""",
					Library1().Replace("internal interface IThing { }", "").Replace("IEnumerable<IThing>", "IEnumerable<Lib0.IThing>"),
				],
				"""
				using Awaiten;

				namespace MyCode;

				internal sealed class Thing : Lib0.IThing { }

				[Container]
				[Import(typeof(Lib.BrewModule))]
				[Singleton<Thing, Lib0.IThing>]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT252") && diagnostic.Contains("an empty collection of 'Lib0.IThing'") && diagnostic.Contains("although 'MyCode.MyContainer' registers it"))
				.Because("the container's own registration of the element type is passed by a container in the module's assembly too");
		}

		[Test]
		public async Task AModuleOfAnAssemblyThatGrantsNothing_RecordsNothing()
		{
			GeneratorResult library = Generator.Run(Library1().Replace("[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"Library2\")]", ""));

			await That(library.Diagnostics).IsEmpty();
			await That(library.Sources["Awaiten.ModuleScan.Lib.BrewModule.g.cs"]).DoesNotContain("EmptyCollection_")
				.Because("no other assembly can name the element type, so none can register it");
		}

		[Test]
		public async Task AContainerTheModuleGrantsNothing_ReportsTheModuleWithTheReceiverAndTheRegisteringModule()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(), Library2(),], App);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected)
				.Because("the module's factory passes 'Counting' an empty collection, while a container in the module's assembly would pass the other module's 'Thing'");
		}

		[Test]
		public async Task AgainstReferenceAssemblies_ReportsTheSame()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(), Library2(),], App, referenceAssemblies: true);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected)
				.Because("a reference assembly keeps the record, a public constant, and the registration of the other module");
		}

		[Test]
		public async Task AContainerTheSecondLibraryGrantsOnly_Reports()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(), Library2(Friend),], App);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected)
				.Because("the container still cannot name the element type, so it calls the module's factory that passes the empty collection");
		}

		[Test]
		public async Task AnElementTheOtherModuleBuildsThroughAHandle_Reports()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(), Library2(thing: "public sealed class Thing : Lib.IThing { }"),], App);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected)
				.Because("the container registers the element through the handle of the other module, which the module's factory never receives");
		}

		[Test]
		public async Task AnElementTheOtherModuleProducesThroughAFactoryMember_Reports()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[
					Library1(),
					Library2(registration: "[Singleton<Thing, Lib.IThing>(Factory = nameof(ThingModule.Create))]")
						.Replace("public static partial class ThingModule { }", "public static partial class ThingModule { internal static Thing Create() => new(); }"),
				],
				App);

			await That(result.Diagnostics).HasSingle().Which.IsEqualTo(Expected)
				.Because("a registration a member produces holds an element as well as one the container constructs");
		}

		[Test]
		public async Task ADecoratorOfAnInternalService_IsReportedToo()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[
					"""
					using System.Collections.Generic;
					using Awaiten;

					[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Library2")]

					namespace Lib;

					public interface IBrew { }
					internal interface IThing { }
					internal interface IBeanStore { }
					internal sealed class BeanStore : IBeanStore { }
					internal sealed class CountingStore : IBeanStore { public CountingStore(IBeanStore inner, IEnumerable<IThing> things) { } }
					internal sealed class Brew : IBrew { public Brew(IBeanStore store) { } }

					[Module]
					[Singleton<BeanStore, IBeanStore>]
					[Singleton<Brew, IBrew>]
					[Decorate<CountingStore, IBeanStore>]
					public static partial class BrewModule { }
					""",
					Library2(),
				],
				App);

			await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT252") && diagnostic.Contains("'Lib.BrewModule' passes 'Lib.CountingStore' an empty collection of 'Lib.IThing'"))
				.Because("the factory of a decorator of an internal service, which the container reaches through a handle, passes the empty collection too");
		}

		[Test]
		public async Task AContainerBothLibrariesGrant_PassesTheRegistration()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(Friend), Library2(Friend),], App);

			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("new global::Lib.Counting(Root.ResolveBrew(__s.__root), new global::Lib.IThing[] { Root.ResolveThing(__s.__root) })")
				.Because("the container builds 'Counting' itself and passes every registration of the element type, as the module's assembly does");
		}

		[Test]
		public async Task AContainerTheFirstLibraryGrantsOnly_DoesNotReportAwt252()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(Friend), Library2(),], App);

			await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT104") && diagnostic.Contains("'Lib2.Thing'"))
				.Because("the container builds 'Counting' itself, so it never calls the factory that passes the empty collection, and reports instead that it cannot construct the internal 'Thing'");
		}

		[Test]
		public async Task AllInOneAssembly_PassesTheRegistration()
		{
			GeneratorResult result = Generator.Run("""
				using System.Collections.Generic;
				using Awaiten;

				namespace Lib
				{
					public interface IBrew { }
					internal interface IThing { }
					internal sealed class Brew : IBrew { }
					internal sealed class Counting : IBrew { public Counting(IBrew inner, IEnumerable<IThing> things) { } }

					[Module]
					[Singleton<Brew, IBrew>]
					[Decorate<Counting, IBrew>]
					public static partial class BrewModule { }
				}

				namespace Lib2
				{
					internal sealed class Thing : Lib.IThing { }

					[Module]
					[Singleton<Thing, Lib.IThing>]
					public static partial class ThingModule { }
				}

				namespace MyCode
				{
					[Container]
					[Import(typeof(Lib.BrewModule))]
					[Import(typeof(Lib2.ThingModule))]
					public static partial class MyContainer;
				}
				""");

			await That(result.Diagnostics).IsEmpty();
			await That(Container(result)).Contains("new global::Lib.Counting(Root.ResolveBrew(__s.__root), new global::Lib.IThing[] { Root.ResolveThing(__s.__root) })");
		}

		[Test]
		public async Task APublicElementType_IsPassedFromTheContainer()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1().Replace("internal interface IThing", "public interface IThing"), Library2(),], App);

			await That(result.Diagnostics).IsEmpty();
			await That(Regex.IsMatch(Container(result), @"Decorator_Counting_[0-9a-f]{8}\(Root\.ResolveIBrew\(__s\.__root\), new global::Lib\.IThing\[\] \{ Root\.Resolve\w+\(__s\.__root\) \}\)")).IsTrue()
				.Because("the module's factory takes a collection of a public element type from the container, which passes the other module's registration, the remedy the diagnostic names");
		}

		[Test]
		public async Task ARegistrationUnderAnotherKey_IsNotReported()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(), Library2(registration: "[Singleton<Thing, Lib.IThing>(Key = \"spare\")]"),], App);

			await That(result.Diagnostics).IsEmpty()
				.Because("the unkeyed collection never holds a keyed registration, so the graph is the one the module's assembly builds");
		}

		[Test]
		public async Task AKeyedCollection_IsReportedForARegistrationUnderItsKey()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[Library1(parameter: "[FromKey(\"spare\")] IEnumerable<IThing> things"), Library2(registration: "[Singleton<Thing, Lib.IThing>(Key = \"spare\")]"),],
				App);

			await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT252") && diagnostic.Contains("an empty collection of 'Lib.IThing (key: spare)'") && diagnostic.Contains("make 'Lib.IThing' public"));
		}

		[Test]
		public async Task AContainerThatImportsNoRegistration_IsNotReported()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain([Library1(), Library2(),], App.Replace("[Import(typeof(Lib2.ThingModule))]", ""));

			await That(result.Diagnostics).IsEmpty()
				.Because("without a registration of the element type the collection is empty in the module's assembly too");
		}

		[Test]
		public async Task AModuleInheritedThroughExtends_IsReportedWhenTheExtendingContainerImportsTheRegistration()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssemblyChain(
				[
					Library1(), Library2(), """
					                        using Awaiten;

					                        namespace Base;

					                        [Container]
					                        [Import(typeof(Lib.BrewModule))]
					                        public static partial class BaseContainer;
					                        """,
				],
				"""
				using Awaiten;

				namespace MyCode;

				[Container(Extends = typeof(Base.BaseContainer))]
				[Import(typeof(Lib2.ThingModule))]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).HasSingle().Which.Satisfies(diagnostic => diagnostic.Contains("AWT252") && diagnostic.Contains("'Lib.BrewModule' passes 'Lib.Counting' an empty collection of 'Lib.IThing'"))
				.Because("the extending container builds the inherited decorator through the module's factory too, while it registers the element itself");
		}

		[Test]
		public async Task AModuleBuiltByAnOlderGenerator_IsNotReported()
		{
			(Compilation generated, _) = Generator.RunGenerator(Library1(), [], [], "Library1");
			SyntaxTree[] olderOutput = generated.SyntaxTrees
				.Select(tree => tree.WithChangedText(Microsoft.CodeAnalysis.Text.SourceText.From(Regex.Replace(tree.GetText().ToString(), "public const string EmptyCollection_[^;]*;", ""))))
				.ToArray();
			MetadataReference library1 = Generator.EmitToReference(CSharpCompilation.Create(
				"Library1", olderOutput, References.For([]), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)));
			(Compilation library2, _) = Generator.RunGenerator(Library2(), [library1,], [], "Library2");
			(Compilation output, GeneratorDriverRunResult run) = Generator.RunGenerator(App, [library1, Generator.EmitToReference(library2),], []);

			await That(run.Diagnostics.Concat(output.GetDiagnostics()).Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning)).IsEmpty()
				.Because("a module without the record behaves as before, so the container calls its factory without a diagnostic");
		}
	}
}
