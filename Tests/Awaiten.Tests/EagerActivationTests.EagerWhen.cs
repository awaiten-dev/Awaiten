using System.Threading;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

public partial class EagerActivationTests
{
	[NotInParallel(nameof(EagerActivationTests))]
	public partial class EagerWhen
	{
		[Test]
		public async Task PredicateTrue_BuildsTheSingletonAtRootCreationInRegistrationOrder()
		{
			Switch.Reset(true);

			using OrderedEagerWhenContainer.Root container = new();

			await That(Probe.Constructed).IsEqualTo(["First", "Client", "Second",])
				.Because("a singleton whose predicate allows it is built like an Eager one, in registration order");
			IClient resolved = container.Resolve<IClient>();
			await That(container.Resolve<IClient>()).IsSameAs(resolved);
			await That(Probe.Constructed).IsEqualTo(["First", "Client", "Second",])
				.Because("the singleton built at startup is cached");
		}

		[Test]
		public async Task PredicateFalse_BuildsNothingUntilResolvedAndThenOneSharedInstance()
		{
			Switch.Reset(false);

			using OrderedEagerWhenContainer.Root container = new();

			await That(Probe.Constructed).IsEqualTo(["First", "Second",])
				.Because("a singleton whose predicate does not allow it stays lazy");
			IClient resolved = container.Resolve<IClient>();
			await That(container.Resolve<IClient>()).IsSameAs(resolved);
			await That(Probe.Constructed).IsEqualTo(["First", "Second", "Client",])
				.Because("it is built on the first resolve and shared afterwards");
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task PredicateTakingAService_ResolvesItAtRootCreation(bool warm)
		{
			Switch.Reset(warm);

			using ServicePredicateContainer.Root container = new();

			string[] built = warm ? ["Settings", "Client",] : ["Settings",];
			await That(Probe.Constructed).IsEqualTo(built)
				.Because("the root resolves the services of the predicate when it is created, to decide whether to build the singleton");
			await That(container.Resolve<Settings>()).IsSameAs(container.Resolve<Settings>());
			await That(Switch.Evaluations).IsEqualTo(1);
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task PredicateSharedWithADecorator_IsEvaluatedOnce(bool monitoring)
		{
			Switch.Reset(monitoring);
			string[] built = monitoring ? ["Client", "MonitoringClient",] : [];

			using (SharedPropertyPredicateContainer.Root container = new())
			{
				await That(Probe.Constructed).IsEqualTo(built)
					.Because("the singleton is built at startup with the decorator only when the predicate allows both");
				await That(container.Resolve<IClient>() is MonitoringClient).IsEqualTo(monitoring);
				await That(Switch.Evaluations).IsEqualTo(1);
			}

			Switch.Reset(monitoring);
			using (SharedMethodPredicateContainer.Root container = new())
			{
				await That(Probe.Constructed).IsEqualTo(built);
				await That(container.Resolve<IClient>() is MonitoringClient).IsEqualTo(monitoring);
			}

			await That(Switch.Evaluations).IsEqualTo(1)
				.Because("an EagerWhen and a When that name the same predicate share its one evaluation per root, also when it takes services");
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task PragmaticMode_AsyncInitializedSingleton_IsInitializedAtRootCreationOnlyWhenAllowed(bool warm)
		{
			Switch.Reset(warm);

			using AsyncEagerWhenContainer.Root container = new();

			string[] built = warm ? ["AsyncEager", "AsyncEager:init",] : [];
			await That(Probe.Constructed).IsEqualTo(built)
				.Because("the root builds and initializes it through the same blocking path as an Eager singleton");
			AsyncEager resolved = await container.ResolveAsync<AsyncEager>(CancellationToken.None);
			await That(resolved.Initialized).IsTrue();
			await That(Probe.Constructed).IsEqualTo(["AsyncEager", "AsyncEager:init",]);
		}

		[Test]
		public async Task ExtendingContainer_ReplacingTheServiceThePredicateTakes_SwitchesItOff()
		{
			Switch.Reset(true);

			using (new EagerWhenBase.Root())
			{
				await That(Probe.Constructed).IsEqualTo(["Client",]);
			}

			Switch.Reset(true);
			using UnmonitoredEagerWhenShop.Root container = new();

			await That(Probe.Constructed).IsEmpty()
				.Because("the inherited predicate takes the service the extending container replaces");
			container.Resolve<IClient>();
			await That(Probe.Constructed).IsEqualTo(["Client",]);
		}

		[Test]
		public async Task ExtendingContainer_ReplacingTheRegistration_DropsEagerWhen()
		{
			Switch.Reset(true);

			using ReplacedEagerWhenShop.Root container = new();

			await That(Probe.Constructed).IsEmpty()
				.Because("the replacement registration has no EagerWhen, and the replaced one is gone with its directives");
			await That(container.Resolve<IClient>()).Is<Client>();
		}

		[Test]
		[Arguments(true)]
		[Arguments(false)]
		public async Task ModuleOfAnotherAssembly_InternalPredicate_DecidesThroughTheWrapper(bool monitoring)
		{
			EagerWhenModuleSwitch.Reset(monitoring);

			using EagerWhenModuleShop.Root container = new();

			string[] built = monitoring ? ["client", "monitored",] : [];
			await That(EagerWhenModuleSwitch.Built).IsEqualTo(built)
				.Because("the client is built at startup with its decorator only when the module's predicate allows it");
			await That(container.Resolve<IEagerWhenClient>().Describe()).IsEqualTo(monitoring ? "monitored(client)" : "client");
			await That(EagerWhenModuleSwitch.Evaluations).IsEqualTo(1)
				.Because("the container calls the module's internal predicate through its public wrapper, once per root for both directives");
		}

		private static class Switch
		{
			public static bool On { get; private set; }

			public static int Evaluations { get; set; }

			public static void Reset(bool on)
			{
				On = on;
				Evaluations = 0;
				Probe.Constructed.Clear();
			}

			public static bool Read()
			{
				Evaluations++;
				return On;
			}
		}

		public interface IClient;

		public sealed class Client : IClient
		{
			public Client() => Probe.Constructed.Add("Client");
		}

		public sealed class MonitoringClient : IClient
		{
			public MonitoringClient(IClient inner)
			{
				Inner = inner;
				Probe.Constructed.Add("MonitoringClient");
			}

			public IClient Inner { get; }
		}

		public sealed class Settings
		{
			public Settings() => Probe.Constructed.Add("Settings");

			public bool Warm { get; } = Switch.On;
		}

		public sealed class Monitoring
		{
			public bool Enabled { get; init; } = true;
		}

		[Container]
		[Singleton<First>(Eager = true)]
		[Singleton<Client, IClient>(EagerWhen = nameof(Warm))]
		[Singleton<Second>(Eager = true)]
		public static partial class OrderedEagerWhenContainer
		{
			private static bool Warm => Switch.On;
		}

		[Container]
		[Singleton<Settings>]
		[Singleton<Client, IClient>(EagerWhen = nameof(Warm))]
		public static partial class ServicePredicateContainer
		{
			private static bool Warm(Settings settings)
			{
				Switch.Evaluations++;
				return settings.Warm;
			}
		}

		[Container]
		[Singleton<Client, IClient>(EagerWhen = nameof(Monitored))]
		[Decorate<MonitoringClient, IClient>(When = nameof(Monitored))]
		public static partial class SharedPropertyPredicateContainer
		{
			private static bool Monitored => Switch.Read();
		}

		[Container]
		[Singleton<Monitoring>]
		[Singleton<Client, IClient>(EagerWhen = nameof(Monitored))]
		[Decorate<MonitoringClient, IClient>(When = nameof(Monitored))]
		public static partial class SharedMethodPredicateContainer
		{
			private static bool Monitored(Monitoring monitoring) => Switch.Read() && monitoring.Enabled;
		}

		[Container(AsyncSafety = AsyncSafety.SyncAfterInit)]
		[AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
		[Singleton<AsyncEager>(EagerWhen = nameof(Warm))]
		public static partial class AsyncEagerWhenContainer
		{
			private static bool Warm => Switch.On;
		}

		[Container]
		[Singleton<Monitoring>]
		[Singleton<Client, IClient>(EagerWhen = nameof(Monitored))]
		public static partial class EagerWhenBase
		{
			internal static bool Monitored(Monitoring monitoring) => monitoring.Enabled;
		}

		[Container(Extends = typeof(EagerWhenBase))]
		[Singleton<Monitoring>(Instance = nameof(Off))]
		public static partial class UnmonitoredEagerWhenShop
		{
			private static Monitoring Off { get; } = new() { Enabled = false, };
		}

		[Container(Extends = typeof(EagerWhenBase))]
		[Singleton<Client, IClient>]
		public static partial class ReplacedEagerWhenShop;

		[Container]
		[Import(typeof(CrossAssemblyEagerWhenModule))]
		public static partial class EagerWhenModuleShop;
	}
}
