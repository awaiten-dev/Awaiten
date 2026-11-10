using System.Linq;

namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt254CollectionOfRelationships
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
		public async Task ReportsAnEnumerableOfLazyWithTheShapeAndTheRemedy()
		{
			GeneratorResult result = RunWithParameter("IEnumerable<Lazy<IClock>>");

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("(11,70): error AWT254: 'MyCode.Reader' cannot be resolved: 'MyCode.Reader' requires 'System.Collections.Generic.IEnumerable<System.Lazy<MyCode.IClock>>', a collection of the relationship 'System.Lazy<MyCode.IClock>', which the container does not build; inject 'System.Collections.Generic.IEnumerable<MyCode.IClock>' to get every registration of 'MyCode.IClock', or 'System.Lazy<MyCode.IClock>' to get a single one")
				.Because("the collection would stay empty although IClock has two registrations, so the error points at the parameter, names the shape and the collection of the service itself as the remedy");
		}

		[Test]
		[Arguments("IEnumerable<Func<IClock>>", "System.Func<MyCode.IClock>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		[Arguments("IEnumerable<Owned<IClock>>", "Awaiten.Owned<MyCode.IClock>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		[Arguments("IEnumerable<Task<IClock>>", "System.Threading.Tasks.Task<MyCode.IClock>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		[Arguments("IEnumerable<Func<string, IClock>>", "System.Func<string, MyCode.IClock>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		[Arguments("IEnumerable<Func<Owned<IClock>>>", "System.Func<Awaiten.Owned<MyCode.IClock>>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		[Arguments("IEnumerable<Func<Task<IClock>>>", "System.Func<System.Threading.Tasks.Task<MyCode.IClock>>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		[Arguments("IEnumerable<Lazy<Task<IClock>>>", "System.Lazy<System.Threading.Tasks.Task<MyCode.IClock>>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		[Arguments("Lazy<IClock>[]", "System.Lazy<MyCode.IClock>", "MyCode.IClock[]")]
		[Arguments("IReadOnlyList<Func<IClock>>", "System.Func<MyCode.IClock>", "System.Collections.Generic.IReadOnlyList<MyCode.IClock>")]
		[Arguments("IReadOnlyCollection<Lazy<IClock>>", "System.Lazy<MyCode.IClock>", "System.Collections.Generic.IReadOnlyCollection<MyCode.IClock>")]
		[Arguments("IList<Owned<IClock>>", "Awaiten.Owned<MyCode.IClock>", "System.Collections.Generic.IList<MyCode.IClock>")]
		[Arguments("ICollection<Func<IClock>>", "System.Func<MyCode.IClock>", "System.Collections.Generic.ICollection<MyCode.IClock>")]
		[Arguments("IAsyncEnumerable<Lazy<IClock>>", "System.Lazy<MyCode.IClock>", "System.Collections.Generic.IAsyncEnumerable<MyCode.IClock>")]
		[Arguments("Task<IReadOnlyList<Func<IClock>>>", "System.Func<MyCode.IClock>", "System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<MyCode.IClock>>")]
		[Arguments("IReadOnlyDictionary<string, Func<IClock>>", "System.Func<MyCode.IClock>", "System.Collections.Generic.IReadOnlyDictionary<string, MyCode.IClock>")]
		[Arguments("Task<IReadOnlyDictionary<string, Owned<IClock>>>", "Awaiten.Owned<MyCode.IClock>", "System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyDictionary<string, MyCode.IClock>>")]
		[Arguments("[FromKey(\"test\")] IEnumerable<Lazy<IClock>>", "System.Lazy<MyCode.IClock>", "System.Collections.Generic.IEnumerable<MyCode.IClock>")]
		public async Task ReportsEveryCollectionShapeOfEveryRelationship(string parameter, string element, string remedy)
		{
			GeneratorResult result = RunWithParameter(parameter);

			await That(result.Diagnostics).HasSingle().Which
				.Contains($"*error AWT254*a collection of the relationship '{element}'*inject '{remedy}' to get every registration of 'MyCode.IClock'*").AsWildcard()
				.Because("every collection and dictionary shape over a relationship would otherwise stay empty, and the remedy is the same shape over the service itself");
		}

		[Test]
		public async Task ReportsAnInjectedProperty()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;
			                                       using System.Collections.Generic;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class SystemClock : IClock { }
			                                       public sealed class Reader { [Inject] public IEnumerable<Lazy<IClock>> Clocks { get; set; } = null!; }

			                                       [Container]
			                                       [Singleton<SystemClock, IClock>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT254*'MyCode.Reader' requires 'System.Collections.Generic.IEnumerable<System.Lazy<MyCode.IClock>>'*").AsWildcard()
				.Because("an injected property is classified like a constructor parameter, so the empty collection is reported there too");
		}

		[Test]
		public async Task ReportsWhenTheTargetIsNotRegisteredEither()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;
			                                       using System.Collections.Generic;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader(IEnumerable<Lazy<IClock>> clocks) { } }

			                                       [Container]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.Contains("*error AWT254*").AsWildcard()
				.Because("a collection of relationships is never filled, whether or not the wrapped service has registrations");
		}

		[Test]
		public async Task DoesNotReportWhenTheRelationshipTypeIsRegisteredAsAService()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;
			                                       using System.Collections.Generic;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class SystemClock : IClock { }
			                                       public sealed class Reader { public Reader(IEnumerable<Func<IClock>> clocks) { } }

			                                       [Container]
			                                       [Singleton<Func<IClock>>(Factory = nameof(CreateClockFactory))]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer
			                                       {
			                                           private static Func<IClock> CreateClockFactory() => () => new SystemClock();
			                                       }
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("a registered Func<IClock> is a service of its own, so the collection holds that registration");
			await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).Contains("new global::MyCode.Reader(new global::System.Func<global::MyCode.IClock>[] { Root.Resolve")
				.Because("the collection materializes the registered Func<IClock>");
		}

		[Test]
		public async Task DoesNotReportAnExplicitlyRegisteredCollectionShape()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;
			                                       using System.Collections.Generic;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader(IEnumerable<Lazy<IClock>> clocks) { } }

			                                       [Container]
			                                       [Singleton<IEnumerable<Lazy<IClock>>>(Factory = nameof(CreateClocks))]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer
			                                       {
			                                           private static IEnumerable<Lazy<IClock>> CreateClocks() => Array.Empty<Lazy<IClock>>();
			                                       }
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("a registered collection shape is resolved as that registration, not synthesized");
		}

		[Test]
		public async Task DoesNotReportATypeDispatchDictionary()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;
			                                       using System;
			                                       using System.Collections.Generic;

			                                       namespace MyCode;

			                                       public interface IHandler { }
			                                       public interface IHandler<T> : IHandler { }
			                                       public sealed class OrderHandler : IHandler<int> { }
			                                       public sealed class Router { public Router(IReadOnlyDictionary<Type, Func<IHandler>> handlers) { } }

			                                       [Container]
			                                       [Transient<OrderHandler, IHandler<int>>]
			                                       [Singleton<Router>]
			                                       public static partial class MyContainer
			                                       {
			                                       }
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("a Type-keyed dictionary of Func values is the runtime-type dispatch dictionary, which builds one Func per closing");
		}

		[Test]
		public async Task DoesNotReportACollectionOfServices()
		{
			GeneratorResult result = RunWithParameter("IEnumerable<IClock>");

			await That(result.Diagnostics).IsEmpty()
				.Because("the collection of the service itself is the supported shape");
		}

		[Test]
		public async Task ReportsATypedResolveWithTheCollectionOfTheServiceAsTheRemedy()
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
			                                                           		root.Resolve<IEnumerable<Lazy<IClock>>>();
			                                                           	}
			                                                           }
			                                                           """);

			string[] reports = diagnostics.Where(d => d.Contains("AWT218")).ToArray();
			await That(reports).HasSingle().Which
				.Contains("*'System.Collections.Generic.IEnumerable<System.Lazy<MyCode.IClock>>' has no unkeyed registration*the container does not build a collection of the relationship 'System.Lazy<MyCode.IClock>'; resolve 'System.Collections.Generic.IEnumerable<MyCode.IClock>' to get every registration of 'MyCode.IClock', or 'System.Lazy<MyCode.IClock>' to get a single one").AsWildcard()
				.Because("the call throws at run time, and the remedy is the collection of the service, not a registration of the relationship");
		}

		[Test]
		public async Task DoesNotBlameTheRelationshipForATypedResolveWhenTheRelationshipTypeIsRegisteredUnderTheKey()
		{
			string[] diagnostics = await Analyzer.Run<AwaitenAnalyzer>("""
			                                                           using Awaiten;
			                                                           using System;
			                                                           using System.Collections.Generic;

			                                                           namespace MyCode;

			                                                           public interface IClock { }
			                                                           public sealed class SystemClock : IClock { }

			                                                           [Container]
			                                                           [Singleton<Func<IClock>>(Key = "test", Factory = nameof(CreateClockFactory))]
			                                                           public static partial class MyContainer
			                                                           {
			                                                           	private static Func<IClock> CreateClockFactory() => () => new SystemClock();
			                                                           }

			                                                           public static class Usage
			                                                           {
			                                                           	public static void Use()
			                                                           	{
			                                                           		using MyContainer.Root root = new();
			                                                           		root.Resolve<IEnumerable<Func<IClock>>>("test");
			                                                           	}
			                                                           }
			                                                           """);

			string[] reports = diagnostics.Where(d => d.Contains("AWT218")).ToArray();
			await That(reports).HasSingle().Which
				.Contains("*has no registration under the key \"test\"*the container builds a collection under a key only for injection, not for a typed resolve*").AsWildcard()
				.Because("a [FromKey(\"test\")] collection of the registered Func<IClock> has a member, so the relationship is not what keeps a typed resolve from it, the key is");
		}
	}
}
