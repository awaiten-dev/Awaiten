namespace Awaiten.SourceGenerators.Tests;

/// <summary>
///     The generated shape of a decorator with a <c>When</c> predicate: one flag per distinct predicate on the
///     <c>Root</c>, read in its constructor for a parameterless predicate or evaluated once on first use for one that
///     takes services, and a guard at the top of each conditional link's resolver that returns the inner link when the
///     flag is off. A container without a predicate generates nothing of it.
/// </summary>
public class ConditionalDecoratorTests
{
	[Test]
	public async Task ParameterlessPredicate_IsReadInTheRootConstructorBeforeEagerActivation()
	{
		GeneratorResult result = Generator.Run("""
		                                       using Awaiten;

		                                       namespace MyCode;

		                                       public interface IClient { }
		                                       public sealed class Client : IClient { }
		                                       public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

		                                       [Container]
		                                       [Singleton<Client, IClient>(Eager = true)]
		                                       [Decorate<MonitoringClient, IClient>(When = nameof(MonitoringEnabled))]
		                                       public static partial class MyContainer
		                                       {
		                                       	private static bool MonitoringEnabled() => true;
		                                       }
		                                       """);

		await That(result.Diagnostics).IsEmpty();
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
		await That(source).Contains("internal readonly bool __when0;");
		await That(source.IndexOf("__when0 = global::MyCode.MyContainer.MonitoringEnabled();", StringComparison.Ordinal))
			.IsGreaterThan(0).And.IsLessThan(source.IndexOf("ResolveMonitoringClient(this);", StringComparison.Ordinal))
			.Because("the flag is set before the eager singleton is built through its outermost link");
		int guard = source.IndexOf("if (!__s.__root.__when0)", StringComparison.Ordinal);
		await That(guard).IsGreaterThan(0)
			.And.IsLessThan(source.IndexOf("return Root.ResolveClient(__s.__root);", StringComparison.Ordinal))
			.And.IsLessThan(source.IndexOf("if (__s._monitoringClient is not null)", StringComparison.Ordinal))
			.Because("an off link returns its inner link before it builds or caches the decorator");
		await That(source).Contains("internal static global::MyCode.IClient ResolveMonitoringClient(Root __s)")
			.Because("a conditional link returns the decorated service, which both the decorator and its inner convert to");
	}

	[Test]
	public async Task PropertyAndFieldPredicates_AreReadWithoutACall()
	{
		GeneratorResult result = Generator.Run("""
		                                       using Awaiten;

		                                       namespace MyCode;

		                                       public interface IClient { }
		                                       public interface IGateway { }
		                                       public sealed class Client : IClient { }
		                                       public sealed class Gateway : IGateway { }
		                                       public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }
		                                       public sealed class TracedGateway : IGateway { public TracedGateway(IGateway inner) { } }

		                                       [Container]
		                                       [Transient<Client, IClient>]
		                                       [Transient<Gateway, IGateway>]
		                                       [Decorate<MonitoringClient, IClient>(When = nameof(Monitoring))]
		                                       [Decorate<TracedGateway, IGateway>(When = nameof(Tracing))]
		                                       public static partial class MyContainer
		                                       {
		                                       	private static bool Monitoring => true;
		                                       	private static readonly bool Tracing = true;
		                                       }
		                                       """);

		await That(result.Diagnostics).IsEmpty();
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
		await That(source).Contains("__when0 = global::MyCode.MyContainer.Monitoring;");
		await That(source).Contains("__when1 = global::MyCode.MyContainer.Tracing;");
	}

	[Test]
	public async Task SharedPredicate_HasOneFlag()
	{
		GeneratorResult result = Generator.Run("""
		                                       using Awaiten;

		                                       namespace MyCode;

		                                       public interface IClient { }
		                                       public interface IGateway { }
		                                       public sealed class Client : IClient { }
		                                       public sealed class Gateway : IGateway { }
		                                       public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }
		                                       public sealed class TracedGateway : IGateway { public TracedGateway(IGateway inner) { } }

		                                       [Container]
		                                       [Transient<Client, IClient>]
		                                       [Transient<Gateway, IGateway>]
		                                       [Decorate<MonitoringClient, IClient>(When = nameof(Monitoring))]
		                                       [Decorate<TracedGateway, IGateway>(When = nameof(Monitoring))]
		                                       public static partial class MyContainer
		                                       {
		                                       	private static bool Monitoring() => true;
		                                       }
		                                       """);

		await That(result.Diagnostics).IsEmpty();
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
		await That(source).Contains("__when0 = global::MyCode.MyContainer.Monitoring();").Exactly(1)
			.Because("a predicate shared by several decorators is evaluated once per root");
		await That(source).DoesNotContain("__when1");
		await That(source).Contains("if (!__s.__root.__when0)").Exactly(2);
	}

	[Test]
	public async Task ServicePredicate_IsEvaluatedOnceOnFirstUseUnderTheGate()
	{
		GeneratorResult result = Generator.Run("""
		                                       using Awaiten;

		                                       namespace MyCode;

		                                       public interface IClient { }
		                                       public sealed class Settings { public bool Monitoring { get; set; } }
		                                       public sealed class Client : IClient { }
		                                       public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

		                                       [Container]
		                                       [Singleton<Settings>]
		                                       [Scoped<Client, IClient>]
		                                       [Decorate<MonitoringClient, IClient>(When = nameof(Monitoring))]
		                                       public static partial class MyContainer
		                                       {
		                                       	private static bool Monitoring(Settings settings) => settings.Monitoring;
		                                       }
		                                       """);

		await That(result.Diagnostics).IsEmpty();
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
		await That(source).Contains("private volatile int __when0;");
		await That(source).Contains("internal static bool __When0(Root __s)");
		int read = source.IndexOf("int __state = __s.__when0;", StringComparison.Ordinal);
		int gate = source.IndexOf("lock (__s.__gate)", read, StringComparison.Ordinal);
		int evaluation = source.IndexOf("__state = global::MyCode.MyContainer.Monitoring(Root.ResolveSettings(__s.__root)) ? 2 : 1;", StringComparison.Ordinal);
		int store = source.IndexOf("__s.__when0 = __state;", StringComparison.Ordinal);
		await That(read).IsGreaterThan(0);
		await That(gate).IsGreaterThan(read);
		await That(evaluation).IsGreaterThan(gate)
			.Because("the predicate resolves its services and is evaluated once under the gate");
		await That(store).IsGreaterThan(evaluation);
		await That(source.IndexOf("return __state == 2;", StringComparison.Ordinal)).IsGreaterThan(store)
			.Because("a later read returns the stored result without taking the lock");
		await That(source).Contains("if (!Root.__When0(__s.__root))");
		await That(source).DoesNotContain("__when0 = global::")
			.Because("a predicate that takes services is not evaluated in the root constructor");
	}

	[Test]
	public async Task AsyncLink_ForwardsToItsInnerInsideTheMemoizedCreator()
	{
		GeneratorResult result = Generator.Run("""
		                                       using System.Threading;
		                                       using System.Threading.Tasks;
		                                       using Awaiten;

		                                       namespace MyCode;

		                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }
		                                       public interface IClient { }
		                                       public sealed class Client : IClient { }
		                                       public sealed class AsyncClient : IClient, IInitializable
		                                       {
		                                       	public AsyncClient(IClient inner) { }
		                                       	public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		                                       }

		                                       [Container]
		                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
		                                       [Scoped<Client, IClient>]
		                                       [Decorate<AsyncClient, IClient>(When = nameof(Enabled))]
		                                       public static partial class MyContainer
		                                       {
		                                       	private static bool Enabled() => true;
		                                       }
		                                       """);

		await That(result.Diagnostics).IsEmpty();
		string source = result.Sources["Awaiten.MyCode.MyContainer.g.cs"];
		int creator = source.IndexOf("async global::System.Threading.Tasks.Task<global::MyCode.IClient> CreateAsyncClientAsync()", StringComparison.Ordinal);
		int guard = source.IndexOf("if (!__s.__root.__when0)", StringComparison.Ordinal);
		await That(creator).IsGreaterThan(0);
		await That(guard).IsGreaterThan(creator)
			.And.IsLessThan(source.IndexOf("new global::MyCode.AsyncClient(ResolveClient(__s))", StringComparison.Ordinal))
			.Because("the forwarded inner is memoized like a built decorator, so the scope caches one answer");
		await That(source).Contains("return ResolveClient(__s);");
	}

	[Test]
	public async Task ContainerWithoutPredicates_GeneratesNoPredicateCode()
	{
		GeneratorResult result = Generator.Run("""
		                                       using System.Threading;
		                                       using System.Threading.Tasks;
		                                       using Awaiten;

		                                       namespace MyCode;

		                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }
		                                       public interface IClient { }
		                                       public interface IHandler<T> { }
		                                       public sealed class Client : IClient { }
		                                       public sealed class Backup : IClient { }
		                                       public sealed class Handler<T> : IHandler<T> { }
		                                       public sealed class TracingHandler<T> : IHandler<T> { public TracingHandler(IHandler<T> inner) { } }
		                                       public sealed class Consumer { public Consumer(IHandler<string> handler, IClient[] clients) { } }
		                                       public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }
		                                       public sealed class AsyncClient : IClient, IInitializable
		                                       {
		                                       	public AsyncClient(IClient inner) { }
		                                       	public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
		                                       }

		                                       [Container]
		                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
		                                       [Singleton<Client, IClient>(Eager = true)]
		                                       [Scoped<Backup, IClient>(Key = "backup")]
		                                       [Transient(typeof(Handler<>), typeof(IHandler<>))]
		                                       [Transient<Consumer>]
		                                       [Decorate<MonitoringClient, IClient>(AllKeys = true)]
		                                       [Decorate<AsyncClient, IClient>(Keys = new object[] { "backup", })]
		                                       [Decorate(typeof(TracingHandler<>), typeof(IHandler<>))]
		                                       public static partial class MyContainer
		                                       {
		                                       }
		                                       """);

		await That(result.Diagnostics).IsEmpty();
		await That(result.Sources["Awaiten.MyCode.MyContainer.g.cs"]).DoesNotContain("__when").IgnoringCase()
			.Because("only a decorator with a predicate adds a flag or a guard, so every other container generates exactly what it did before");
	}

	public class Analysis
	{
		[Test]
		public async Task AsyncDecoratorThatMayBeOff_KeepsAnEagerSingletonAsync()
		{
			GeneratorResult result = Generator.Run("""
			                                       using System.Threading;
			                                       using System.Threading.Tasks;
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IInitializable { Task InitializeAsync(CancellationToken cancellationToken); }
			                                       public interface IClient { }
			                                       public sealed class Client : IClient { }
			                                       public sealed class AsyncClient : IClient, IInitializable
			                                       {
			                                       	public AsyncClient(IClient inner) { }
			                                       	public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
			                                       }

			                                       [Container]
			                                       [AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
			                                       [Singleton<Client, IClient>(Eager = true)]
			                                       [Decorate<AsyncClient, IClient>(When = nameof(Enabled))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static bool Enabled() => false;
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT161*").AsWildcard()
				.Because("a link that may be off counts as present, so its async initialization still taints the eager singleton");
		}

		[Test]
		public async Task DecoratorThatMayBeOff_IsStillCheckedForCaptiveDependencies()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClient { }
			                                       public sealed class Session { }
			                                       public sealed class Client : IClient { }
			                                       public sealed class SessionClient : IClient { public SessionClient(IClient inner, Session session) { } }

			                                       [Container]
			                                       [Scoped<Session>]
			                                       [Singleton<Client, IClient>]
			                                       [Decorate<SessionClient, IClient>(When = nameof(Enabled))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static bool Enabled() => false;
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT105*").AsWildcard()
				.Because("the graph is validated with the decorator in place, whatever the predicate returns");
		}

		[Test]
		public async Task PredicateTakingAServiceThatNeedsTheDecoratedService_IsACycle()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClient { }
			                                       public sealed class Settings { public Settings(IClient client) { } }
			                                       public sealed class Client : IClient { }
			                                       public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

			                                       [Container]
			                                       [Singleton<Settings>]
			                                       [Singleton<Client, IClient>]
			                                       [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static bool Enabled(Settings settings) => true;
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT102*").AsWildcard()
				.Because("evaluating the predicate would resolve the decorated service, which needs the predicate");
		}

		[Test]
		public async Task PredicateTakingAnUnregisteredService_IsAMissingDependency()
		{
			GeneratorResult result = Generator.Run("""
			                                       using Awaiten;

			                                       namespace MyCode;

			                                       public interface IClient { }
			                                       public sealed class Settings { }
			                                       public sealed class Client : IClient { }
			                                       public sealed class MonitoringClient : IClient { public MonitoringClient(IClient inner) { } }

			                                       [Container]
			                                       [Singleton<Client, IClient>]
			                                       [Decorate<MonitoringClient, IClient>(When = nameof(Enabled))]
			                                       public static partial class MyContainer
			                                       {
			                                       	private static bool Enabled(Settings settings) => true;
			                                       }
			                                       """);

			await That(result.Diagnostics).Contains("*AWT101*MyContainer.Enabled' requires 'MyCode.Settings'*").AsWildcard();
		}
	}
}
