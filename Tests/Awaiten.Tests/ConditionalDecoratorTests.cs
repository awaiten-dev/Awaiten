using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     A decorator whose <c>When</c> predicate decides, once per root, whether it is applied. The predicates read static
///     switches, so every nested class shares one parallel key.
/// </summary>
public partial class ConditionalDecoratorTests
{
	[NotInParallel(nameof(ConditionalDecoratorTests))]
	public class ParameterlessPredicate
	{
		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task EagerSingleton_IsBuiltAsTheFlagReadAtRootCreationDecides(bool enabled)
		{
			Switches.Reset(enabled);

			using (EagerShop.Root root = new())
			{
				Switches.Monitoring = !enabled;
				await That(Log.Entries).IsEqualTo(enabled ? ["Client", "Monitoring",] : ["Client",])
					.Because("eager activation builds the chain through the outermost link, which skips the decorator when it is off");
				IClient client = root.Resolve<IClient>();
				await That(client is MonitoringClient).IsEqualTo(enabled)
					.Because("the predicate is read once when the root is created, not per resolve");
				await That(root.Resolve<IClient[]>()[0]).IsSameAs(client);
				using EagerShop.Scope scope = root.CreateScope();
				await That(scope.Resolve<IClient>()).IsSameAs(client);
			}

			await That(Log.Entries.Contains("Monitoring:disposed")).IsEqualTo(enabled)
				.Because("a decorator that is off is never built, so it is never tracked for disposal");
		}

		[Test]
		public async Task Predicate_IsEvaluatedOncePerRoot_EvenWhenSeveralDecoratorsShareIt()
		{
			Switches.Reset(true);

			using (SharedPredicateShop.Root root = new())
			{
				await That(Switches.Evaluations).IsEqualTo(1)
					.Because("a parameterless predicate is evaluated when the root is created");
				_ = root.Resolve<IClient>();
				_ = root.Resolve<IGateway>();
				using SharedPredicateShop.Scope scope = root.CreateScope();
				_ = scope.Resolve<IClient>();
				await That(Switches.Evaluations).IsEqualTo(1)
					.Because("both decorators share the predicate, which is evaluated once per root");
				await That(root.Resolve<IGateway>()).Is<TracedGateway>();
			}

			Switches.Monitoring = false;
			using SharedPredicateShop.Root second = new();

			await That(Switches.Evaluations).IsEqualTo(2);
			await That(second.Resolve<IGateway>()).Is<Gateway>()
				.Because("each root evaluates the predicate for itself");
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task PropertyAndFieldPredicates_DecideLikeAMethod(bool enabled)
		{
			Switches.Reset(enabled);
			MemberShop.FieldSwitch = enabled;

			using MemberShop.Root root = new();

			await That(root.Resolve<IClient>() is MonitoringClient).IsEqualTo(enabled);
			await That(root.Resolve<IGateway>() is TracedGateway).IsEqualTo(enabled);
		}

		[Test]
		public async Task MiddleLinkOff_KeepsTheOrderOfTheOtherLinks()
		{
			Switches.Reset(false);

			using ChainShop.Root root = new();
			IClient client = root.Resolve<IClient>();

			await That(client).Is<OuterClient>();
			await That(((OuterClient)client).Inner).Is<InnerClient>()
				.Because("the middle link forwards to its inner link when its predicate is false");
			await That(((InnerClient)((OuterClient)client).Inner).Inner).Is<Client>();
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task OffDecorator_NeverResolvesItsOwnDependencies(bool enabled)
		{
			Switches.Reset(enabled);

			using DependentShop.Root root = new();
			IClient client = root.Resolve<IClient>();

			await That(client is MeteredClient).IsEqualTo(enabled);
			await That(Log.Entries.Contains("Meter")).IsEqualTo(enabled)
				.Because("the guard returns before the decorator's dependencies are resolved");
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task KeyedDecorator_FollowsTheFlagUnderItsKey(bool enabled)
		{
			Switches.Reset(enabled);

			using KeyedShop.Root root = new();
			KeyedConsumer consumer = root.Resolve<KeyedConsumer>();

			await That(consumer.Backup is MonitoringClient).IsEqualTo(enabled);
			await That(consumer.ByKey["backup"]).IsSameAs(consumer.Backup)
				.Because("the keyed dictionary points at the same chain");
			await That(consumer.Primary).Is<Client>()
				.Because("only the selected key is decorated");
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task CollectionAndComposite_FollowTheFlagMemberByMember(bool enabled)
		{
			Switches.Reset(enabled);

			using CompositeShop.Root root = new();
			IClient[] members = root.Resolve<IClient[]>();
			IClient front = root.Resolve<IClient>();

			await That(members).HasCount(2);
			await That(members.All(member => member is MonitoringClient)).IsEqualTo(enabled);
			await That(front).Is<ClientFanOut>();
			await That(((ClientFanOut)front).Members.All(member => member is MonitoringClient)).IsEqualTo(enabled)
				.Because("the composite fronts the members as the flag decorates them");
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task ScopedDecorator_ReadsTheFlagOfItsRoot(bool enabled)
		{
			Switches.Reset(enabled);

			using ScopedShop.Root root = new();
			Switches.Monitoring = !enabled;
			using ScopedShop.Scope first = root.CreateScope();
			using ScopedShop.Scope second = root.CreateScope();
			IClient client = first.Resolve<IClient>();

			await That(client is MonitoringClient).IsEqualTo(enabled);
			await That(first.Resolve<IClient>()).IsSameAs(client);
			await That(second.Resolve<IClient>() is MonitoringClient).IsEqualTo(enabled)
				.Because("every scope reads the flag its root evaluated");
			await That(second.Resolve<IClient>()).IsNotSameAs(client);
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task OpenGenericDecorator_FollowsTheFlagForEveryClosing(bool enabled)
		{
			Switches.Reset(enabled);

			using OpenGenericShop.Root root = new();

			await That(root.Resolve<IHandler<string>>() is TracingHandler<string>).IsEqualTo(enabled);
			await That(root.Resolve<IHandler<int>>() is TracingHandler<int>).IsEqualTo(enabled);
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task AsyncInitializedDecorator_StaysAsyncButIsOnlyBuiltAndInitializedWhenOn(bool enabled)
		{
			Switches.Reset(enabled);
			using AsyncShop.Root root = new();
			using AsyncShop.Scope scope = root.CreateScope();

			IClient client = await scope.ResolveAsync<IClient>();

			await That(client is AsyncMonitoringClient).IsEqualTo(enabled);
			await That(Log.Entries.Contains("AsyncMonitoring:init")).IsEqualTo(enabled);
			await That(() => scope.Resolve<IClient>()).Throws<InvalidOperationException>()
				.Because("the graph is validated with the decorator, so the service stays async-tainted even when it is off");
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task PrivateDecorator_FollowsTheFlag(bool enabled)
		{
			Switches.Reset(enabled);

			using PrivateDecoratorShop.Root root = new();

			await That(root.Resolve<IClient>().GetType().Name == "HiddenMonitoringClient").IsEqualTo(enabled);
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task DecoratorWithRuntimeArguments_FollowsTheFlag(bool enabled)
		{
			Switches.Reset(enabled);

			using ArgShop.Root root = new();
			IClient client = root.Resolve<Func<string, IClient>>()("eu");

			await That(client is RegionClient).IsEqualTo(enabled);
			if (enabled)
			{
				await That(((RegionClient)client).Region).IsEqualTo("eu");
			}
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task RequestingTypeFactory_IsDecoratedAsTheFlagDecides(bool enabled)
		{
			Switches.Reset(enabled);

			using RequestingTypeShop.Root root = new();
			IClient client = root.Resolve<ClientConsumer>().Client;

			await That(client is MonitoringClient).IsEqualTo(enabled);
			await That(enabled ? ((NamedClient)((MonitoringClient)client).Inner).RequestedBy : ((NamedClient)client).RequestedBy)
				.IsEqualTo(enabled ? nameof(MonitoringClient) : null)
				.Because("an off link has no consumer to name, so the factory sees a top-level request");
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task InheritedDecorator_IsDecidedByThePredicateOfTheBase(bool enabled)
		{
			Switches.Reset(enabled);

			using ExtendingShop.Root root = new();
			IClient client = root.Resolve<IClient>();

			await That(client).Is<OuterClient>()
				.Because("the extending container's own decorator stays unconditional");
			await That(((OuterClient)client).Inner is MonitoringClient).IsEqualTo(enabled)
				.Because("the inherited decorator stays inside and follows the base's predicate");
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task ModuleOfAnotherAssembly_DecoratesPublicAndInternalServicesAsItsPublicPredicateDecides(bool enabled)
		{
			ConditionalModuleSwitch.Enabled = enabled;

			using CrossAssemblyShop.Root root = new();

			await That(root.Resolve<IConditionalGateway>().Describe()).IsEqualTo(enabled ? "traced(gateway)" : "gateway");
			await That(root.Resolve<IConditionalGatewayReader>().Gateway).IsEqualTo(enabled ? "audited(gateway)" : "gateway")
				.Because("a decorator of an internal service decorates its handle, whose link the predicate guards too");
		}
	}

	[NotInParallel(nameof(ConditionalDecoratorTests))]
	public class ServicePredicate
	{
		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task PredicateTakingASingleton_IsEvaluatedOnFirstUseWithTheSharedInstance(bool enabled)
		{
			Switches.Reset(enabled);

			using SingletonPredicateShop.Root root = new();

			await That(Switches.Evaluations).IsEqualTo(0)
				.Because("a predicate that takes services is evaluated when a link first needs it");
			IClient client = root.Resolve<IClient>();
			_ = root.Resolve<IClient>();
			using SingletonPredicateShop.Scope scope = root.CreateScope();
			_ = scope.Resolve<IClient>();

			await That(client is MonitoringClient).IsEqualTo(enabled);
			await That(Switches.Evaluations).IsEqualTo(1);
			await That(Switches.SeenSettings).IsSameAs(root.Resolve<Settings>())
				.Because("the predicate receives the singleton the root shares");
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task PredicateTakingAnInstanceRegistration_DecidesFromIt(bool enabled)
		{
			Switches.Reset(false);
			InstancePredicateShop.Current = new Settings { Monitoring = enabled, };

			using InstancePredicateShop.Root root = new();

			await That(root.Resolve<IClient>() is MonitoringClient).IsEqualTo(enabled);
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task PredicateTakingAnImportedService_DecidesFromTheExternalResolver(bool enabled)
		{
			Switches.Reset(false);

			using ImportedPredicateShop.Root root = new(new SettingsResolver(new Settings { Monitoring = enabled, }));

			await That(root.Resolve<IClient>() is MonitoringClient).IsEqualTo(enabled);
		}

		[Test]
		public async Task ConcurrentFirstResolves_EvaluateThePredicateOnce()
		{
			Switches.Reset(true);
			Switches.Delay = TimeSpan.FromMilliseconds(50);
			using SingletonPredicateShop.Root root = new();
			using Barrier barrier = new(16);

			IClient[] clients = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Factory.StartNew(() =>
			{
				barrier.SignalAndWait();
				using SingletonPredicateShop.Scope scope = root.CreateScope();
				return scope.Resolve<IClient>();
			}, TaskCreationOptions.LongRunning)));

			await That(Switches.Evaluations).IsEqualTo(1)
				.Because("concurrent first uses wait for the one evaluation instead of evaluating again");
			await That(clients.All(client => client is MonitoringClient)).IsTrue();
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task EagerSingleton_EvaluatesThePredicateWhenItIsBuilt_AfterTheServicesItTakes(bool enabled)
		{
			Switches.Reset(enabled);

			using EagerSingletonPredicateShop.Root root = new();

			await That(Log.Entries).IsEqualTo(enabled ? ["Settings", "Predicate", "Client", "Monitoring",] : ["Settings", "Predicate", "Client",])
				.Because("eager activation of the decorated singleton evaluates its predicate, which first builds the singleton it takes");
			await That(root.Resolve<IClient>() is MonitoringClient).IsEqualTo(enabled);
			await That(Switches.Evaluations).IsEqualTo(1);
		}

		[Test]
		public async Task ThrowingPredicate_IsEvaluatedAgainOnTheNextUse()
		{
			Switches.Reset(true);
			Switches.Throws = true;
			using SingletonPredicateShop.Root root = new();

			await That(() => root.Resolve<IClient>()).Throws<InvalidOperationException>().WithMessage("predicate failed");
			Switches.Throws = false;

			await That(root.Resolve<IClient>()).Is<MonitoringClient>()
				.Because("a predicate that threw stored no result, so the next use evaluates it again");
			await That(Switches.Evaluations).IsEqualTo(2);
		}
	}

	internal static class Log
	{
		public static List<string> Entries { get; } = new();
	}

	internal static class Switches
	{
		public static bool Monitoring { get; set; }

		public static int Evaluations { get; set; }

		public static Settings? SeenSettings { get; set; }

		public static TimeSpan Delay { get; set; }

		public static bool Throws { get; set; }

		public static void Reset(bool monitoring)
		{
			Monitoring = monitoring;
			Evaluations = 0;
			SeenSettings = null;
			Delay = TimeSpan.Zero;
			Throws = false;
			Log.Entries.Clear();
		}

		public static bool Read()
		{
			Evaluations++;
			return Monitoring;
		}

		public static bool Read(Settings settings)
		{
			Log.Entries.Add("Predicate");
			Thread.Sleep(Delay);
			Evaluations++;
			SeenSettings = settings;
			if (Throws)
			{
				throw new InvalidOperationException("predicate failed");
			}

			return settings.Monitoring;
		}
	}

	public sealed class Settings
	{
		public Settings() => Log.Entries.Add("Settings");

		public bool Monitoring { get; init; }
	}

	private sealed class SettingsResolver(Settings settings) : IExternalResolver
	{
		public bool TryResolve(Type serviceType, object? key, [NotNullWhen(true)] out object? instance)
		{
			instance = serviceType == typeof(Settings) ? settings : null;
			return instance is not null;
		}
	}

	public interface IClient;

	public interface IGateway;

	public interface IHandler<T>;

	public sealed class Client : IClient
	{
		public Client() => Log.Entries.Add("Client");
	}

	public sealed class SecondClient : IClient;

	public sealed class MonitoringClient(IClient inner) : IClient, IDisposable
	{
		public IClient Inner { get; } = Logged(inner);

		public void Dispose() => Log.Entries.Add("Monitoring:disposed");

		private static IClient Logged(IClient inner)
		{
			Log.Entries.Add("Monitoring");
			return inner;
		}
	}

	public sealed class InnerClient(IClient inner) : IClient
	{
		public IClient Inner { get; } = inner;
	}

	public sealed class OuterClient(IClient inner) : IClient
	{
		public IClient Inner { get; } = inner;
	}

	public sealed class Meter
	{
		public Meter() => Log.Entries.Add("Meter");
	}

	public sealed class MeteredClient(IClient inner, Meter meter) : IClient
	{
		public IClient Inner { get; } = inner;

		public Meter Meter { get; } = meter;
	}

	public sealed class RegionClient(IClient inner, [Arg] string region) : IClient
	{
		public IClient Inner { get; } = inner;

		public string Region { get; } = region;
	}

	public sealed class NamedClient(string? requestedBy) : IClient
	{
		public string? RequestedBy { get; } = requestedBy;
	}

	public sealed class ClientConsumer(IClient client)
	{
		public IClient Client { get; } = client;
	}

	public sealed class KeyedConsumer([FromKey("backup")] IClient backup, IClient primary, IReadOnlyDictionary<string, IClient> byKey)
	{
		public IClient Backup { get; } = backup;

		public IClient Primary { get; } = primary;

		public IReadOnlyDictionary<string, IClient> ByKey { get; } = byKey;
	}

	public sealed class ClientFanOut(IClient[] members) : IClient
	{
		public IClient[] Members { get; } = members;
	}

	public sealed class AsyncMonitoringClient(IClient inner) : IClient, IInitializable
	{
		public IClient Inner { get; } = inner;

		public Task InitializeAsync(CancellationToken cancellationToken)
		{
			Log.Entries.Add("AsyncMonitoring:init");
			return Task.CompletedTask;
		}
	}

	public sealed class Gateway : IGateway;

	public sealed class TracedGateway(IGateway inner) : IGateway
	{
		public IGateway Inner { get; } = inner;
	}

	public sealed class Handler<T> : IHandler<T>;

	public sealed class TracingHandler<T>(IHandler<T> inner) : IHandler<T>
	{
		public IHandler<T> Inner { get; } = inner;
	}

	[Container]
	[Singleton<Client, IClient>(Eager = true)]
	[Decorate<MonitoringClient, IClient>(When = nameof(MonitoringEnabled))]
	public static partial class EagerShop
	{
		private static bool MonitoringEnabled() => Switches.Monitoring;
	}

	[Container]
	[Singleton<Client, IClient>]
	[Singleton<Gateway, IGateway>]
	[Decorate<MonitoringClient, IClient>(When = nameof(MonitoringEnabled))]
	[Decorate<TracedGateway, IGateway>(When = nameof(MonitoringEnabled))]
	public static partial class SharedPredicateShop
	{
		private static bool MonitoringEnabled() => Switches.Read();
	}

	[Container]
	[Singleton<Client, IClient>]
	[Transient<Gateway, IGateway>]
	[Decorate<MonitoringClient, IClient>(When = nameof(PropertySwitch))]
	[Decorate<TracedGateway, IGateway>(When = nameof(FieldSwitch))]
	public static partial class MemberShop
	{
		internal static bool FieldSwitch;

		private static bool PropertySwitch => Switches.Monitoring;
	}

	[Container]
	[Transient<Client, IClient>]
	[Decorate<OuterClient, IClient>(Order = 3)]
	[Decorate<MonitoringClient, IClient>(Order = 2, When = nameof(MonitoringEnabled))]
	[Decorate<InnerClient, IClient>(Order = 1)]
	public static partial class ChainShop
	{
		private static bool MonitoringEnabled => Switches.Monitoring;
	}

	[Container]
	[Transient<Client, IClient>]
	[Singleton<Meter>]
	[Decorate<MeteredClient, IClient>(When = nameof(MonitoringEnabled))]
	public static partial class DependentShop
	{
		private static bool MonitoringEnabled => Switches.Monitoring;
	}

	[Container]
	[Singleton<Client, IClient>]
	[Singleton<SecondClient, IClient>(Key = "backup")]
	[Transient<KeyedConsumer>]
	[Decorate<MonitoringClient, IClient>(Keys = new object?[] { "backup", }, When = nameof(MonitoringEnabled))]
	public static partial class KeyedShop
	{
		private static bool MonitoringEnabled => Switches.Monitoring;
	}

	[Container]
	[Singleton<Client, IClient>]
	[Singleton<SecondClient, IClient>]
	[Composite<ClientFanOut, IClient>]
	[Decorate<MonitoringClient, IClient>(When = nameof(MonitoringEnabled))]
	public static partial class CompositeShop
	{
		private static bool MonitoringEnabled => Switches.Monitoring;
	}

	[Container]
	[Scoped<Client, IClient>]
	[Decorate<MonitoringClient, IClient>(When = nameof(MonitoringEnabled))]
	public static partial class ScopedShop
	{
		private static bool MonitoringEnabled => Switches.Monitoring;
	}

	[Container]
	[Transient(typeof(Handler<>), typeof(IHandler<>))]
	[Transient<Consumer<string>>]
	[Transient<Consumer<int>>]
	[Decorate(typeof(TracingHandler<>), typeof(IHandler<>), When = nameof(TracingEnabled))]
	public static partial class OpenGenericShop
	{
		private static bool TracingEnabled => Switches.Monitoring;
	}

	public sealed class Consumer<T>(IHandler<T> handler)
	{
		public IHandler<T> Handler { get; } = handler;
	}

	[Container]
	[AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
	[Scoped<Client, IClient>]
	[Decorate<AsyncMonitoringClient, IClient>(When = nameof(MonitoringEnabled))]
	public static partial class AsyncShop
	{
		private static bool MonitoringEnabled() => Switches.Monitoring;
	}

	[Container]
	[Transient<Client, IClient>]
	[Decorate<HiddenMonitoringClient, IClient>(When = nameof(MonitoringEnabled))]
	public static partial class PrivateDecoratorShop
	{
		private static bool MonitoringEnabled => Switches.Monitoring;

		private sealed class HiddenMonitoringClient(IClient inner) : IClient
		{
			public IClient Inner { get; } = inner;
		}
	}

	[Container]
	[Transient<Client, IClient>]
	[Decorate<RegionClient, IClient>(When = nameof(MonitoringEnabled))]
	public static partial class ArgShop
	{
		private static bool MonitoringEnabled => Switches.Monitoring;
	}

	[Container]
	[Transient<IClient>(Factory = nameof(CreateClient))]
	[Transient<ClientConsumer>]
	[Decorate<MonitoringClient, IClient>(When = nameof(MonitoringEnabled))]
	public static partial class RequestingTypeShop
	{
		private static bool MonitoringEnabled => Switches.Monitoring;

		private static IClient CreateClient([RequestingType] Type? requestingType) => new NamedClient(requestingType?.Name);
	}

	[Container]
	[Transient<Client, IClient>]
	[Decorate<MonitoringClient, IClient>(When = nameof(MonitoringEnabled))]
	public static partial class BaseShop
	{
		internal static bool MonitoringEnabled => Switches.Monitoring;
	}

	[Container(Extends = typeof(BaseShop))]
	[Decorate<OuterClient, IClient>]
	public static partial class ExtendingShop;

	[Container]
	[Import(typeof(CrossAssemblyConditionalDecoratorModule))]
	public static partial class CrossAssemblyShop;

	[Container]
	[Singleton<Settings>]
	[Scoped<Client, IClient>]
	[Decorate<MonitoringClient, IClient>(When = nameof(MonitoringEnabled))]
	public static partial class SingletonPredicateShop
	{
		private static bool MonitoringEnabled(Settings settings) => Switches.Read(settings) || Switches.Monitoring;
	}

	[Container]
	[Singleton<Settings>(Instance = nameof(Current))]
	[Singleton<Client, IClient>]
	[Decorate<MonitoringClient, IClient>(When = nameof(MonitoringEnabled))]
	public static partial class InstancePredicateShop
	{
		internal static Settings Current { get; set; } = new();

		private static bool MonitoringEnabled(Settings settings) => settings.Monitoring;
	}

	[Container]
	[ImportService<Settings>]
	[Singleton<Client, IClient>]
	[Decorate<MonitoringClient, IClient>(When = nameof(MonitoringEnabled))]
	public static partial class ImportedPredicateShop
	{
		private static bool MonitoringEnabled(Settings settings) => settings.Monitoring;
	}

	[Container]
	[Singleton<Settings>]
	[Singleton<Client, IClient>(Eager = true)]
	[Decorate<MonitoringClient, IClient>(When = nameof(MonitoringEnabled))]
	public static partial class EagerSingletonPredicateShop
	{
		private static bool MonitoringEnabled(Settings settings) => Switches.Read(settings) || Switches.Monitoring;
	}
}
