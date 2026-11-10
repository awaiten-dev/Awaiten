namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt258ImportedCollection
	{
		private static GeneratorResult Run(string imports, string consumer = "public Reader() { }")
			=> Generator.Run($$"""
			                   using System.Collections.Generic;
			                   using System.Threading.Tasks;
			                   using Awaiten;

			                   namespace MyCode;

			                   public interface IClock { }
			                   public sealed class Reader { {{consumer}} }

			                   [Container]
			                   {{imports}}
			                   [Singleton<Reader>]
			                   public static partial class MyContainer;
			                   """);

		[Test]
		[Arguments("IEnumerable<IClock>")]
		[Arguments("IClock[]")]
		[Arguments("IReadOnlyList<IClock>")]
		[Arguments("IReadOnlyCollection<IClock>")]
		[Arguments("IList<IClock>")]
		[Arguments("ICollection<IClock>")]
		[Arguments("IAsyncEnumerable<IClock>")]
		[Arguments("IReadOnlyDictionary<string, IClock>")]
		[Arguments("Task<IEnumerable<IClock>>")]
		[Arguments("Task<IClock[]>")]
		[Arguments("Task<IReadOnlyDictionary<string, IClock>>")]
		public async Task ReportsEachCollectionShape(string collection)
		{
			GeneratorResult result = Run($"[ImportService<{collection}>]");

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("(11,2): error AWT258: 'MyCode.MyContainer' imports the collection '*', but a collection is always built from the container's own registrations, so the external resolver is never asked for it; import 'MyCode.IClock' and depend on it directly, or register the members of the collection").AsWildcard()
				.Because("a collection holds only the container's own registrations, so the import is never routed");
		}

		[Test]
		public async Task ATypeDispatch_NamesItsBaseInterface()
		{
			GeneratorResult result = Run("[ImportService<IReadOnlyDictionary<System.Type, System.Func<IClock>>>]");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("imports the collection 'System.Collections.Generic.IReadOnlyDictionary<System.Type, System.Func<MyCode.IClock>>', * import 'MyCode.IClock' and depend on it directly").AsWildcard()
				.Because("a type dispatch holds the registrations of its base interface, which is the service to import");
		}

		[Test]
		[Arguments("typeof(IEnumerable<IClock>)")]
		[Arguments("typeof(IEnumerable<>)")]
		[Arguments("typeof(IReadOnlyDictionary<,>)")]
		public async Task ReportsTheTypeofForm(string type)
		{
			GeneratorResult result = Run($"[ImportService({type})]");

			await That(result.Diagnostics).HasSingle().Which.Contains("error AWT258: 'MyCode.MyContainer' imports the collection");
		}

		[Test]
		public async Task ADependencyOnTheCollection_IsReportedOnlyAtTheImport()
		{
			GeneratorResult result = Run("[ImportService<IEnumerable<IClock>>]", "public Reader(IEnumerable<IClock> clocks) { }");

			await That(result.Diagnostics).HasSingle().Which.Contains("AWT258")
				.Because("the dependency resolves the container's own, empty collection, and the import is the one mistake");
		}

		[Test]
		public async Task ARelationshipOverTheCollection_IsReportedAsAwt256Besides()
		{
			GeneratorResult result = Run("[ImportService<IEnumerable<IClock>>]", "public Reader(System.Lazy<IEnumerable<IClock>> clocks) { }");

			await That(result.Diagnostics).HasCount(2);
			await That(result.Diagnostics).Contains("*AWT258*").AsWildcard();
			await That(result.Diagnostics).Contains("*AWT256*").AsWildcard()
				.Because("the relationship over a collection is a mistake of the consumer of its own, and the dropped import gives no AWT242");
		}

		[Test]
		public async Task ACollectionOverAnImportedElement_KeepsAwt250()
		{
			GeneratorResult result = Run("[ImportService<IClock>]\n[ImportService<IEnumerable<IClock>>]", "public Reader(IEnumerable<IClock> clocks) { }");

			await That(result.Diagnostics).HasCount(2);
			await That(result.Diagnostics).Contains("*AWT258*").AsWildcard();
			await That(result.Diagnostics).Contains("*warning AWT250*").AsWildcard()
				.Because("the collection stays empty without the import of the collection, which is a mistake of its own");
		}

		[Test]
		public async Task AnImportOfTheElement_IsNotReported()
		{
			GeneratorResult result = Run("[ImportService<IClock>]", "public Reader(IClock clock) { }");

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task AnImportOfAnotherGenericType_IsNotReported()
		{
			GeneratorResult result = Run("[ImportService<List<IClock>>]\n[ImportService<Task<IClock>>]", "public Reader(List<IClock> clocks) { }");

			await That(result.Diagnostics).IsEmpty()
				.Because("a concrete list and a task are no collection shapes the container builds");
		}

		[Test]
		public async Task AnImportOfAModuleInTheSameCompilation_IsReportedAtTheModulesAttribute()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }

			                                       [Module]
			                                       [ImportService<IEnumerable<IClock>>]
			                                       public static class ClockModule;

			                                       [Container]
			                                       [Import(typeof(ClockModule))]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).HasSingle().Which.StartsWith("(9,2): error AWT258: 'MyCode.ClockModule' imports the collection 'System.Collections.Generic.IEnumerable<MyCode.IClock>'");
		}

		[Test]
		public async Task AnImportOfAModuleOfAnotherAssembly_IsReportedAtTheImport()
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly(
				"""
				using System.Collections.Generic;
				using Awaiten;

				namespace Lib;

				public interface IClock { }

				[Module]
				[ImportService<IEnumerable<IClock>>]
				public static class ClockModule;
				""",
				"""
				using Awaiten;

				namespace MyCode;

				[Container]
				[Import(typeof(Lib.ClockModule))]
				public static partial class MyContainer;
				""");

			await That(result.Diagnostics).HasSingle().Which.StartsWith("(6,2): error AWT258: 'Lib.ClockModule' imports the collection");
		}

		[Test]
		public async Task AnImportOfAnExtendedBase_IsReportedOnlyInTheBase()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }

			                                       [Container]
			                                       [ImportService<IEnumerable<IClock>>]
			                                       public static partial class BaseContainer;

			                                       [Container(Extends = typeof(BaseContainer))]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).HasSingle().Which.Contains("'MyCode.BaseContainer' imports the collection")
				.Because("the extending container inherits the import, which the base reports");
		}
	}
}
