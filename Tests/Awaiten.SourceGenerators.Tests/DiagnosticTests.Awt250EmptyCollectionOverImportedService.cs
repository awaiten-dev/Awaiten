namespace Awaiten.SourceGenerators.Tests;

public partial class DiagnosticTests
{
	public class Awt250EmptyCollectionOverImportedService
	{
		[Test]
		[Arguments("IEnumerable<IClock>")]
		[Arguments("IClock[]")]
		[Arguments("IReadOnlyList<IClock>")]
		[Arguments("IReadOnlyCollection<IClock>")]
		[Arguments("IList<IClock>")]
		[Arguments("ICollection<IClock>")]
		[Arguments("IAsyncEnumerable<IClock>")]
		[Arguments("Task<IReadOnlyList<IClock>>")]
		[Arguments("IReadOnlyDictionary<string, IClock>")]
		[Arguments("Task<IReadOnlyDictionary<string, IClock>>")]
		public async Task ReportsACollectionOfAClosedImport(string collection)
		{
			GeneratorResult result = Generator.Run($$"""
			                                         using System.Collections.Generic;
			                                         using System.Threading.Tasks;
			                                         using Awaiten;

			                                         namespace MyCode;

			                                         public interface IClock { }
			                                         public sealed class Reader { public Reader({{collection}} clocks) { } }

			                                         [Container]
			                                         [ImportService<IClock>]
			                                         [Singleton<Reader>]
			                                         public static partial class MyContainer;
			                                         """);

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("*warning AWT250: 'MyCode.Reader' depends on a collection of 'MyCode.IClock', which is always empty: the container imports 'MyCode.IClock' from its external resolver, which serves only a direct dependency, and a collection holds only the container's own registrations, including variance matches, of which there are none; depend on 'MyCode.IClock' directly to receive the host's instance").AsWildcard()
				.Because("the collection is built from the container's own registrations, never from the host, so it is empty at run time");
		}

		[Test]
		public async Task ReportsAnInjectedPropertyOfAClosedImport()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { [Inject] public IEnumerable<IClock> Clocks { get; init; } = null!; }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("*warning AWT250: 'MyCode.Reader' depends on a collection of 'MyCode.IClock'*").AsWildcard();
		}

		[Test]
		public async Task ReportsAKeyedCollectionOfAClosedImport()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader([FromKey("utc")] IEnumerable<IClock> clocks) { } }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("*warning AWT250: 'MyCode.Reader' depends on a collection of 'MyCode.IClock (key: utc)'*").AsWildcard()
				.Because("a keyed direct dependency of an imported service is routed with its key, so a keyed collection of it is empty too");
		}

		[Test]
		public async Task ReportsACollectionOfAClosingOfAnOpenImport()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface ILog<T> { }
			                                       public sealed class Reader { public Reader(IEnumerable<ILog<Reader>> logs) { } }

			                                       [Container]
			                                       [ImportService(typeof(ILog<>))]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("*warning AWT250: 'MyCode.Reader' depends on a collection of 'MyCode.ILog<MyCode.Reader>'*").AsWildcard()
				.Because("a direct ILog<Reader> would be routed by the open import, and the container registers no closing for the collection");
		}

		[Test]
		public async Task ReportsACollectionOfAnInheritedImportOnlyOnce()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader(IEnumerable<IClock> clocks) { } }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Singleton<Reader>]
			                                       public static partial class Shop;

			                                       [Container(Extends = typeof(Shop))]
			                                       public static partial class Branch;
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("*warning AWT250: 'MyCode.Reader' depends on a collection of 'MyCode.IClock'*").AsWildcard()
				.Because("the base reports its own consumer, and the extending container does not repeat it");
		}

		[Test]
		public async Task DoesNotReportARegisteredClosingOfAnOpenImport()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface ILog<T> { }
			                                       public sealed class ReaderLog : ILog<Reader> { }
			                                       public sealed class Reader { public Reader(IEnumerable<ILog<Reader>> logs) { } }

			                                       [Container]
			                                       [ImportService(typeof(ILog<>))]
			                                       [Singleton<ReaderLog, ILog<Reader>>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("the container registers the closing, so the collection has a member");
		}

		[Test]
		public async Task DoesNotReportWhenVarianceAddsAMember()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IHandler<in T> { }
			                                       public sealed class AnyHandler : IHandler<object> { }
			                                       public sealed class Reader { public Reader(IEnumerable<IHandler<string>> handlers) { } }

			                                       [Container]
			                                       [ImportService<IHandler<string>>]
			                                       [Singleton<AnyHandler, IHandler<object>>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("the registered IHandler<object> is a variance match of IHandler<string>, so the collection is not empty");
		}

		[Test]
		public async Task DoesNotReportUnderImportServicesAlone()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader(IEnumerable<IClock> clocks) { } }

			                                       [Container]
			                                       [ImportServices]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("the blanket fall-through declares no service host-owned, so an empty collection of an unregistered type is normal there");
		}

		[Test]
		public async Task DoesNotReportADirectDependency()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public sealed class Reader { public Reader(IClock clock) { } }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).IsEmpty();
		}

		[Test]
		public async Task DoesNotReportADecoratorPredicate()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       public interface IClient { }
			                                       public sealed class Client : IClient { }
			                                       public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Scoped<Client, IClient>]
			                                       [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			                                       public static partial class MyContainer
			                                       {
			                                           private static bool Enabled(IEnumerable<IClock> clocks) => true;
			                                       }
			                                       """);

			await That(result.Diagnostics).HasSingle().Which
				.IsEqualTo("*error AWT248*").AsWildcard()
				.Because("a predicate takes no collection at all, so the error is the only report");
		}

		[Test]
		public async Task PragmaSuppressesIt()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Collections.Generic;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClock { }
			                                       #pragma warning disable AWT250
			                                       public sealed class Reader { public Reader(IEnumerable<IClock> clocks) { } }
			                                       #pragma warning restore AWT250

			                                       [Container]
			                                       [ImportService<IClock>]
			                                       [Singleton<Reader>]
			                                       public static partial class MyContainer;
			                                       """);

			await That(result.Diagnostics).IsEmpty()
				.Because("an intended empty collection can be suppressed where it is declared");
		}
	}
}
