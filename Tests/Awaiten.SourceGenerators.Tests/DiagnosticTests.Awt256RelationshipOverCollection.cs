using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt256RelationshipOverCollection
	{
		private static GeneratorResult RunWithParameter(string parameter, string registrations = "")
			=> Generator.Run($$"""
			                   using Awaiten;
			                   using System;
			                   using System.Collections.Generic;
			                   using System.Threading.Tasks;

			                   namespace MyCode;

			                   public interface IClock { }
			                   public sealed class SystemClock : IClock { }
			                   public sealed class TestClock : IClock { }
			                   public sealed class Reader { public Reader({{parameter}} clocks) { } }

			                   [Container]
			                   [Singleton<SystemClock, IClock>]
			                   [Singleton<TestClock, IClock>]
			                   [Singleton<TestClock, IClock>(Key = "test")]
			                   {{registrations}}
			                   [Singleton<Reader>]
			                   public static partial class MyContainer
			                   {
			                   }
			                   """);

		[Test]
		public async Task ReportsAFuncOverAnEnumerableWithTheCollectionAndTheRemedy()
		{
			GeneratorResult result = RunWithParameter("Func<IEnumerable<IClock>>");

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("(11,70): error AWT256: 'MyCode.Reader' cannot be resolved: 'MyCode.Reader' requires 'System.Func<System.Collections.Generic.IEnumerable<MyCode.IClock>>', a relationship over the collection 'System.Collections.Generic.IEnumerable<MyCode.IClock>', which the container does not build, because a relationship wraps a single service; inject 'System.Collections.Generic.IEnumerable<MyCode.IClock>' to get every registration, or register a service that takes 'System.Collections.Generic.IEnumerable<MyCode.IClock>' and inject the relationship over that service")
				.Because("the collection resolves directly, so claiming it is not registered (AWT101) would mislead, and the error names the collection to inject instead");
		}

		[Test]
		[Arguments("Lazy<IEnumerable<IClock>>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		[Arguments("Owned<IEnumerable<IClock>>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		[Arguments("Func<IReadOnlyList<IClock>>", "System.Collections.Generic.IReadOnlyList<MyCode.IClock>")]
		[Arguments("Func<IClock[]>", "MyCode.IClock[]")]
		[Arguments("Func<string, IEnumerable<IClock>>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		[Arguments("Func<Owned<IEnumerable<IClock>>>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		[Arguments("Func<IAsyncEnumerable<IClock>>", "System.Collections.Generic.IAsyncEnumerable<MyCode.IClock>")]
		[Arguments("Func<Task<IReadOnlyList<IClock>>>", "System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<MyCode.IClock>>")]
		[Arguments("Lazy<Task<IEnumerable<IClock>>>", "System.Threading.Tasks.Task<System.Collections.Generic.IEnumerable<MyCode.IClock>>")]
		[Arguments("Task<Owned<IEnumerable<IClock>>>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		[Arguments("Lazy<IReadOnlyDictionary<string, IClock>>", "System.Collections.Generic.IReadOnlyDictionary<string, MyCode.IClock>")]
		[Arguments("[FromKey(\"test\")] Func<IEnumerable<IClock>>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		public async Task ReportsEveryRelationshipOverEveryCollectionShape(string parameter, string collection)
		{
			GeneratorResult result = RunWithParameter(parameter);

			await That(result.Diagnostics).HasSingle().Which
				.Contains($"*error AWT256*a relationship over the collection '{collection}'*inject '{collection}' to get every registration*").AsWildcard()
				.Because("no relationship over a synthesized collection is built, and the remedy is the collection itself, awaited where the relationship awaits");
		}

		[Test]
		public async Task ReportsAnOptionalInjectedProperty()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;
			                                       using System.Collections.Generic;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class SystemClock : IClock { }
			                                       public sealed class Reader { [Inject(Optional = true)] public Lazy<IEnumerable<IClock>> Clocks { get; set; } }

			                                       [Container]
			                                       [Singleton<SystemClock, IClock>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT256*'MyCode.Reader' requires 'System.Lazy<System.Collections.Generic.IEnumerable<MyCode.IClock>>'*").AsWildcard()
				.Because("Optional would otherwise leave the property unset silently although IClock is registered, as no registration of the collection is ever synthesized");
		}

		[Test]
		[Arguments("[Inject(Optional = true)]", "Func<IEnumerable<IClock>>")]
		[Arguments("[Inject(Deferred = true, Optional = true)]", "Lazy<IReadOnlyList<IClock>>")]
		public async Task ReportsAnOptionalPropertyOfAnotherAssemblysModule(string inject, string relationship)
		{
			GeneratorResult result = Generator.RunWithGeneratedReferencedAssembly($$"""
			                                                                        using System;
			                                                                        using System.Collections.Generic;
			                                                                        using Awaiten;

			                                                                        namespace Lib;

			                                                                        public interface IGreeter { }
			                                                                        public interface IClock { }
			                                                                        public sealed class SystemClock : IClock { }

			                                                                        internal sealed class Greeter : IGreeter
			                                                                        {
			                                                                            {{inject}} public {{relationship}} Clocks { get; set; } = null!;
			                                                                        }

			                                                                        [Module]
			                                                                        [Singleton<SystemClock, IClock>]
			                                                                        [Transient<Greeter, IGreeter>]
			                                                                        public static partial class GreetingModule { }
			                                                                        """, """
			                                                                             using Awaiten;

			                                                                             namespace App;

			                                                                             [Container]
			                                                                             [Import(typeof(Lib.GreetingModule))]
			                                                                             public static partial class MyContainer;
			                                                                             """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains($"*error AWT256*'Lib.Greeter' requires 'System.*', a relationship over the collection*").AsWildcard()
				.Because("the module's generated factory or setter fills the property, so it is reported exactly as when the container builds the implementation itself, instead of leaving it unset");
		}

		[Test]
		public async Task DoesNotReportARegisteredCollection()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;
			                                       using System.Collections.Generic;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class SystemClock : IClock { }
			                                       public sealed class Reader { public Reader(Func<IEnumerable<IClock>> clocks) { } }

			                                       [Container]
			                                       [Singleton<IEnumerable<IClock>>(Factory = nameof(CreateClocks))]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer
			                                       {
			                                           private static IEnumerable<IClock> CreateClocks() => new IClock[] { new SystemClock(), };
			                                       }
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("a registered collection shape is a service of its own, so a relationship over it is an ordinary relationship");
		}

		[Test]
		public async Task ReportsARelationshipOverAnImportedCollectionBesideTheImport()
		{
			GeneratorResult result = RunWithParameter("Func<IEnumerable<IClock>>", "[ImportService<IEnumerable<IClock>>]");

			await That(result.Diagnostics).HasCount(2);
			await That(result.Diagnostics).Contains("*error AWT258*").AsWildcard();
			await That(result.Diagnostics).Contains("*error AWT256*").AsWildcard()
				.Because("a collection cannot be imported (AWT258), so the relationship is over the container's own collection, which it does not build, and no AWT242");
		}

		[Test]
		public async Task ReportsAnUnregisteredServiceUnderARelationshipAsAwt101()
		{
			GeneratorResult result = RunWithParameter("Func<List<IClock>>");

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT101*").AsWildcard()
				.Because("a List<T> is no collection shape the container synthesizes, so it is an ordinary unregistered service");
		}

		[Test]
		public async Task ReportsATypedResolveWithTheCollectionAsTheRemedy()
		{
			string[] diagnostics = await Analyzer.Run<AwaitenAnalyzer>("""
			                                                           using Awaiten;
			                                                           using System;
			                                                           using System.Collections.Generic;

			                                                           namespace MyCode;

			                                                           public interface IClock { }
			                                                           public sealed class SystemClock : IClock { }

			                                                           [Container]
			                                                           [Singleton<SystemClock, IClock>]
			                                                           public static partial class MyContainer;

			                                                           public static class Usage
			                                                           {
			                                                           	public static void Use()
			                                                           	{
			                                                           		using MyContainer.Root root = new();
			                                                           		root.Resolve<Func<IEnumerable<IClock>>>();
			                                                           	}
			                                                           }
			                                                           """);

			string[] reports = diagnostics.Where(d => d.Contains("AWT218")).ToArray();
			await That(reports).HasSingle().Which
				.Contains("*'System.Func<System.Collections.Generic.IEnumerable<MyCode.IClock>>' has no unkeyed registration*the container does not build a relationship over the collection 'System.Collections.Generic.IEnumerable<MyCode.IClock>'; resolve 'System.Collections.Generic.IEnumerable<MyCode.IClock>' itself").AsWildcard()
				.Because("the call throws at run time, and the remedy is the collection, not a registration of the relationship");
		}
	}
}
