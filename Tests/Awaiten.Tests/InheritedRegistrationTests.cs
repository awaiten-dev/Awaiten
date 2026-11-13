using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Awaiten.Tests.ContainerSupport;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of <c>[Inherited]</c>: a replacement's factory on an extending container takes the registration
///     of the base it replaces, which keeps its lifetime, hooks, async initialization and disposal but serves nothing else.
/// </summary>
public partial class InheritedRegistrationTests
{
	public class Fallback
	{
		[Test]
		public async Task WithoutADouble_TheFactoryReturnsTheProductionInstance()
		{
			await using TestStoreShop.Root shop = new(new SettingsResolver(new StoreSettings()));

			await That(Inner(shop.Resolve<IStore>())).Is<DiskStore>();
		}

		[Test]
		public async Task WithADouble_TheFactoryReturnsTheDouble()
		{
			FakeStore fake = new();
			await using TestStoreShop.Root shop = new(new SettingsResolver(new StoreSettings { Store = fake, }));

			await That(Inner(shop.Resolve<IStore>())).IsSameAs(fake);
		}

		[Test]
		public async Task WithADouble_TheProductionInstanceIsNeverBuilt()
		{
			await using TestStoreShop.Root shop = new(new SettingsResolver(new StoreSettings { Store = new FakeStore(), }));

			shop.Resolve<IStore>();

			await That(shop.Resolve<BuildLog>().Entries).IsEmpty()
				.Because("the factory never calls the Func, and the inherited registration's Eager is dropped");
		}

		[Test]
		public async Task WithADouble_AnInheritedEagerWhenSingletonIsNeverBuilt()
		{
			await using TestEagerWhenStoreShop.Root shop = new(new SettingsResolver(new StoreSettings { Store = new FakeStore(), }));

			shop.Resolve<IStore>();

			await That(shop.Resolve<BuildLog>().Entries).IsEmpty()
				.Because("the replacement decides startup, so the inherited registration's EagerWhen is dropped like its Eager");
		}

		[Test]
		public async Task TheInheritedHookRunsOnTheProductionInstance()
		{
			await using TestStoreShop.Root shop = new(new SettingsResolver(new StoreSettings()));

			await That(((DiskStore)Inner(shop.Resolve<IStore>())).Activated).IsTrue();
		}

		[Test]
		public async Task TheInheritedDecoratorWrapsTheReplacementOnce()
		{
			await using TestStoreShop.Root shop = new(new SettingsResolver(new StoreSettings()));

			IStore store = shop.Resolve<IStore>();

			await That(store).Is<LoggingStore>();
			await That(((LoggingStore)store).Inner).Is<DiskStore>()
				.Because("the decorator wraps the replacement, not the inherited instance as well");
		}

		[Test]
		public async Task TheKeptRegistrationIsAbsentFromTheCollection()
		{
			await using TestStoreShop.Root shop = new(new SettingsResolver(new StoreSettings()));

			IStore[] stores = shop.Resolve<IStore[]>();

			await That(stores).HasCount(1);
			await That(stores[0]).Is<LoggingStore>();
		}

		[Test]
		public async Task TheKeptRegistrationIsNotAdvertised()
		{
			await using TestStoreShop.Root shop = new(new SettingsResolver(new StoreSettings()));

			await That(((IAwaitenRoot)shop).Registrations.Where(registration => registration.ServiceType == typeof(IStore))).HasCount(1)
				.Because("a host such as the MS.DI bridge must see only the replacement");
		}

		[Test]
		public async Task TheKeptRegistrationKeepsItsSingletonLifetime()
		{
			await using TransientStoreShop.Root shop = new();

			await That(Inner(shop.Resolve<IStore>())).IsSameAs(Inner(shop.Resolve<IStore>()))
				.Because("each transient replacement takes the one production singleton of the root");
			await That(shop.Resolve<BuildLog>().Entries).HasCount(1);
		}

		[Test]
		public async Task TheBaseIsUnchanged()
		{
			await using StoreShop.Root shop = new();

			await That(shop.Resolve<BuildLog>().Entries).HasCount(1)
				.Because("the base builds its Eager store at startup");
			await That(Inner(shop.Resolve<IStore>())).Is<DiskStore>();
		}

		private static IStore Inner(IStore store) => ((LoggingStore)store).Inner;
	}

	public class Disposal
	{
		[Test]
		public async Task TheInheritedInstanceIsDisposedOnceWhenTheFactoryReturnsIt()
		{
			DiskStore production;
			await using (TestStoreShop.Root shop = new(new SettingsResolver(new StoreSettings())))
			{
				production = (DiskStore)((LoggingStore)shop.Resolve<IStore>()).Inner;
			}

			await That(production.Disposals).IsEqualTo(1)
				.Because("the inherited registration owns the production instance, so the factory output is not tracked again");
		}

		[Test]
		public async Task ATransientInheritedInstanceIsDisposedOnceWhenTheFactoryReturnsIt()
		{
			DiskStore production;
			await using (TransientInheritedShop.Root shop = new())
			{
				production = (DiskStore)shop.Resolve<IStore>();
			}

			await That(production.Disposals).IsEqualTo(1);
		}

		[Test]
		public async Task TheDoubleIsDisposedAsFactoryOutput()
		{
			FakeStore fake = new();
			await using (TestStoreShop.Root shop = new(new SettingsResolver(new StoreSettings { Store = fake, })))
			{
				shop.Resolve<IStore>();
			}

			await That(fake.Disposals).IsEqualTo(1);
		}

		[Test]
		public async Task SuppressDisposal_LeavesTheDoubleToItsOwner()
		{
			FakeStore fake = new();
			await using (SuppressedStoreShop.Root shop = new(new SettingsResolver(new StoreSettings { Store = fake, })))
			{
				shop.Resolve<IStore>();
			}

			await That(fake.Disposals).IsEqualTo(0);
		}

		[Test]
		public async Task SuppressDisposal_LeavesTheReturnedInheritedInstanceToItsRegistration()
		{
			DiskStore production;
			await using (SuppressedStoreShop.Root shop = new(new SettingsResolver(new StoreSettings())))
			{
				production = (DiskStore)((LoggingStore)shop.Resolve<IStore>()).Inner;
			}

			await That(production.Disposals).IsEqualTo(1)
				.Because("SuppressDisposal applies to the replacement's own output, while the inherited registration still disposes the production instance");
		}

		[Test]
		public async Task AScopedReplacementLeavesAnInheritedSingletonToTheRoot()
		{
			DiskStore production;
			await using (ScopedOverSingletonShop.Root shop = new())
			{
				await using (IAwaitenScope scope = shop.CreateScope())
				{
					production = (DiskStore)scope.Resolve<IStore>();
				}

				await That(production.Disposals).IsEqualTo(0)
					.Because("the scope only took the root's singleton through its replacement and does not own it");
			}

			await That(production.Disposals).IsEqualTo(1);
		}

		[Test]
		public async Task AFactoryThatThrowsAfterTakingTheInheritedInstanceLeavesItToItsRegistration()
		{
			DiskStore production;
			await using (ThrowingStoreShop.Root shop = new())
			{
				await That(() => shop.Resolve<IStore>()).Throws<InvalidOperationException>();
				production = (DiskStore)shop.Resolve<StoreCapture>().Production!;

				await That(production.Disposals).IsEqualTo(0)
					.Because("the replacement's failure has no output to tear down, and the production singleton stays cached");
			}

			await That(production.Disposals).IsEqualTo(1);
		}
	}

	public class ConsumerOwnership
	{
		[Test]
		public async Task WithoutADouble_TheRootStillDisposesTheProductionSingleton()
		{
			DiskStore production;
			await using (ConsumerOwnedStoreShop.Root shop = new(new SettingsResolver(new StoreSettings())))
			{
				production = (DiskStore)shop.Resolve<Barista>().Take();
			}

			await That(production.Disposals).IsEqualTo(1)
				.Because("the consumer owns only what the replacement builds, not the shared instance it takes through [Inherited]");
		}

		[Test]
		public async Task WithADouble_TheConsumerOwnsTheDouble()
		{
			FakeStore fake = new();
			await using (ConsumerOwnedStoreShop.Root shop = new(new SettingsResolver(new StoreSettings { Store = fake, })))
			{
				await That(shop.Resolve<Barista>().Take()).IsSameAs(fake);
			}

			await That(fake.Disposals).IsEqualTo(0)
				.Because("the consumer owns the double the transient replacement returns, so the root does not dispose it");
		}
	}

	public class Contexts
	{
		[Test]
		public async Task WhenInjectedInto_OnlyTheTargetedConsumerGetsTheReplacement()
		{
			await using ContextTestShop.Root shop = new();

			StoreConsumer consumer = shop.Resolve<StoreConsumer>();

			await That(consumer.Store).Is<WrappedStore>();
			await That(((WrappedStore)consumer.Store).Inner).Is<SpecialStore>()
				.Because("[Inherited] takes the base's registration for the same consumer");
			await That(shop.Resolve<OtherStoreConsumer>().Store).Is<DiskStore>();
			await That(shop.Resolve<IStore>()).Is<DiskStore>();
		}
	}

	public class Keys
	{
		[Test]
		public async Task AKeyedReplacementTakesTheRegistrationWithTheSameKey()
		{
			await using KeyedTestShop.Root shop = new();

			KeyedConsumer consumer = shop.Resolve<KeyedConsumer>();

			await That(consumer.Backup).Is<WrappedStore>();
			await That(((WrappedStore)consumer.Backup).Inner).Is<BackupStore>();
			await That(consumer.Primary).Is<DiskStore>()
				.Because("the unkeyed registration is not replaced");
		}
	}

	public class Async
	{
		[Test]
		public async Task AnAsyncInitializedInheritedRegistrationIsAwaited()
		{
			await using AsyncTestShop.Root shop = new();

			IKiln kiln = await shop.ResolveAsync<IKiln>();

			await That(kiln).Is<Kiln>();
			await That(((Kiln)kiln).Hot).IsTrue();
		}

		[Test]
		public async Task ATaskOfTheInheritedRegistrationIsAwaited()
		{
			await using TaskKilnShop.Root shop = new();

			IKiln kiln = await shop.ResolveAsync<IKiln>();

			await That(kiln).Is<Kiln>();
			await That(((Kiln)kiln).Hot).IsTrue();
			await That(shop.Resolve<BuildLog>().Entries).HasCount(1);
		}

		[Test]
		public async Task ALazyTaskOfTheInheritedRegistrationIsAwaited()
		{
			await using LazyTaskKilnShop.Root shop = new();

			IKiln kiln = await shop.ResolveAsync<IKiln>();

			await That(kiln).Is<Kiln>();
			await That(((Kiln)kiln).Hot).IsTrue();
			await That(shop.Resolve<BuildLog>().Entries).HasCount(1);
		}

		[Test]
		public async Task AnUnusedAsyncInitializedInheritedRegistrationIsNotWarmed()
		{
			await using FakeKilnShop.Root shop = new();

			await shop.InitializeAsync();

			await That(shop.Resolve<BuildLog>().Entries).IsEmpty()
				.Because("only the [Inherited] parameter reaches the replaced kiln, and the factory never calls it");
		}
	}

	public class Ownership
	{
		[Test]
		public async Task AReturnedInheritedInstanceGetsOnlyItsOwnHooksAndInitialization()
		{
			IHookStore store;
			await using (TestHookShop.Root shop = new(new SettingsResolver(new HookSettings())))
			{
				store = await shop.ResolveAsync<IHookStore>();
			}

			await That(store).Is<HookStore>();
			await That(store.Activations).IsEqualTo(1)
				.Because("the inherited registration owns the instance, so the replacement's hook does not run on it again");
			await That(store.Initializations).IsEqualTo(1);
			await That(store.Releases).IsEqualTo(1);
		}

		[Test]
		public async Task ADoubleGetsTheReplacementsHooksAndInitialization()
		{
			FakeHookStore fake = new();
			await using (TestHookShop.Root shop = new(new SettingsResolver(new HookSettings { Store = fake, })))
			{
				await That(await shop.ResolveAsync<IHookStore>()).IsSameAs(fake);
			}

			await That(fake.Activations).IsEqualTo(1);
			await That(fake.Initializations).IsEqualTo(1);
			await That(fake.Releases).IsEqualTo(1);
		}

		[Test]
		public async Task AnInstanceTakenTwiceIsStillOwnedByTheInheritedRegistration()
		{
			DiskStore production;
			await using (TwiceShop.Root shop = new())
			{
				production = (DiskStore)shop.Resolve<IStore>();
			}

			await That(production.Disposals).IsEqualTo(1)
				.Because("every instance the parameter hands out during the build counts, not only the last");
		}
	}

	public class CrossAssembly
	{
		[Test]
		public async Task AModuleInAnotherAssemblyServesItsFactoryRegistration()
		{
			await using LampTestShop.Root shop = new();

			await That(shop.Resolve<IFriendFactoryLamp>().Lit).IsTrue()
				.Because("the replaced registration is built through the module's factory, and the module's hook runs on it");
		}

		[Test]
		public async Task ABaseInAnotherAssemblyServesItsRegistration()
		{
			await using SupportClockTestShop.Root shop = new();

			await That(shop.Resolve<ISupportBaseClock>()).Is<SupportSystemClock>();
		}

		[Test]
		public async Task APrivateImplementationOfABaseInAnotherAssemblyIsBuiltThroughItsFactory()
		{
			await using SupportTerminalTestShop.Root shop = new();

			ISupportAsyncTerminal terminal = await shop.ResolveAsync<ISupportAsyncTerminal>();

			await That(terminal.Token).IsNotNull()
				.Because("the base's private asynchronous hook ran on the inherited instance");
		}
	}

	public sealed class BuildLog
	{
		public List<string> Entries { get; } = [];
	}

	public interface IStore;

	public sealed class DiskStore : IStore, IDisposable
	{
		public DiskStore(BuildLog log) => log.Entries.Add(nameof(DiskStore));

		public bool Activated { get; set; }

		public int Disposals { get; private set; }

		public void Dispose() => Disposals++;
	}

	public sealed class BackupStore : IStore;

	public sealed class FakeStore : IStore, IDisposable
	{
		public int Disposals { get; private set; }

		public void Dispose() => Disposals++;
	}

	public sealed class LoggingStore(IStore inner) : IStore
	{
		public IStore Inner { get; } = inner;
	}

	public sealed class WrappedStore(IStore inner) : IStore
	{
		public IStore Inner { get; } = inner;
	}

	public sealed class SpecialStore : IStore;

	public sealed class StoreConsumer(IStore store)
	{
		public IStore Store { get; } = store;
	}

	public sealed class OtherStoreConsumer(IStore store)
	{
		public IStore Store { get; } = store;
	}

	public sealed class Barista(Func<IStore> stores)
	{
		public IStore Take() => stores();
	}

	public sealed class StoreCapture
	{
		public IStore? Production { get; set; }
	}

	public sealed class KeyedConsumer([FromKey("backup")] IStore backup, IStore primary)
	{
		public IStore Backup { get; } = backup;

		public IStore Primary { get; } = primary;
	}

	public sealed class StoreSettings
	{
		public IStore? Store { get; init; }
	}

	public interface IKiln;

	public sealed class Kiln : IKiln
	{
		public Kiln(BuildLog log) => log.Entries.Add(nameof(Kiln));

		public bool Hot { get; set; }
	}

	public sealed class FakeKiln : IKiln;

	public interface IHookStore
	{
		int Activations { get; set; }

		int Initializations { get; set; }

		int Releases { get; set; }

		Task InitializeAsync();
	}

	public sealed class HookStore : IHookStore
	{
		public int Activations { get; set; }

		public int Initializations { get; set; }

		public int Releases { get; set; }

		public Task InitializeAsync()
		{
			Initializations++;
			return Task.CompletedTask;
		}
	}

	public sealed class FakeHookStore : IHookStore
	{
		public int Activations { get; set; }

		public int Initializations { get; set; }

		public int Releases { get; set; }

		public Task InitializeAsync()
		{
			Initializations++;
			return Task.CompletedTask;
		}
	}

	public sealed class HookSettings
	{
		public IHookStore? Store { get; init; }
	}

	private sealed class SettingsResolver(object settings) : IExternalResolver
	{
		public bool TryResolve(Type serviceType, object? key, [NotNullWhen(true)] out object? instance)
		{
			instance = serviceType == settings.GetType() ? settings : null;
			return instance is not null;
		}
	}

	[Container]
	[AsyncInitializer<IHookStore>(nameof(IHookStore.InitializeAsync))]
	[Singleton<HookStore, IHookStore>(OnActivated = nameof(Activate), OnRelease = nameof(Release))]
	public static partial class HookShop
	{
		internal static void Activate(HookStore store) => store.Activations++;

		internal static void Release(HookStore store) => store.Releases++;
	}

	[Container(Extends = typeof(HookShop))]
	[ImportService<HookSettings>]
	[Singleton<IHookStore>(Factory = nameof(Store), OnActivated = nameof(Activate), OnRelease = nameof(Release))]
	public static partial class TestHookShop
	{
		private static async Task<IHookStore> Store(HookSettings settings, [Inherited] Func<Task<IHookStore>> production)
			=> settings.Store ?? await production();

		private static void Activate(IHookStore store) => store.Activations++;

		private static void Release(IHookStore store) => store.Releases++;
	}

	[Container]
	[Singleton<BuildLog>]
	[Singleton<DiskStore, IStore>(OnActivated = nameof(Activate), Eager = true)]
	[Decorate<LoggingStore, IStore>]
	public static partial class StoreShop
	{
		internal static void Activate(DiskStore store) => store.Activated = true;
	}

	[Container(Extends = typeof(StoreShop))]
	[ImportService<StoreSettings>]
	[Singleton<IStore>(Factory = nameof(Store))]
	public static partial class TestStoreShop
	{
		private static IStore Store(StoreSettings settings, [Inherited] Func<IStore> production) => settings.Store ?? production();
	}

	[Container]
	[Singleton<BuildLog>]
	[Singleton<DiskStore, IStore>(EagerWhen = nameof(Always))]
	public static partial class EagerWhenStoreShop
	{
		internal static bool Always() => true;
	}

	[Container(Extends = typeof(EagerWhenStoreShop))]
	[ImportService<StoreSettings>]
	[Singleton<IStore>(Factory = nameof(Store))]
	public static partial class TestEagerWhenStoreShop
	{
		private static IStore Store(StoreSettings settings, [Inherited] Func<IStore> production) => settings.Store ?? production();
	}

	[Container(Extends = typeof(StoreShop))]
	[ImportService<StoreSettings>]
	[Singleton<IStore>(Factory = nameof(Store), SuppressDisposal = true)]
	public static partial class SuppressedStoreShop
	{
		private static IStore Store(StoreSettings settings, [Inherited] Func<IStore> production) => settings.Store ?? production();
	}

	[Container(Extends = typeof(StoreShop))]
	[Transient<IStore>(Factory = nameof(Store))]
	public static partial class TransientStoreShop
	{
		private static IStore Store([Inherited] Lazy<IStore> production) => production.Value;
	}

	[Container]
	[Singleton<BuildLog>]
	[Transient<DiskStore, IStore>]
	public static partial class TransientShop;

	[Container(Extends = typeof(TransientShop))]
	[Transient<IStore>(Factory = nameof(Store))]
	public static partial class TransientInheritedShop
	{
		private static IStore Store([Inherited] IStore production) => production;
	}

	[Container(Extends = typeof(TransientShop))]
	[Transient<IStore>(Factory = nameof(Store))]
	public static partial class TwiceShop
	{
		private static IStore Store([Inherited] Func<IStore> production)
		{
			IStore first = production();
			production();
			return first;
		}
	}

	[Container]
	[Singleton<BuildLog>]
	[Singleton<DiskStore, IStore>]
	[Singleton<BackupStore, IStore>(Key = "backup")]
	[Singleton<KeyedConsumer>]
	public static partial class KeyedShop;

	[Container(Extends = typeof(KeyedShop))]
	[Singleton<IStore>(Factory = nameof(Backup), Key = "backup")]
	public static partial class KeyedTestShop
	{
		private static WrappedStore Backup([Inherited] IStore production) => new(production);
	}

	[Container]
	[Singleton<BuildLog>]
	[Singleton<Kiln, IKiln>(OnActivated = nameof(HeatAsync))]
	public static partial class AsyncShop
	{
		internal static async Task HeatAsync(Kiln kiln)
		{
			await Task.Yield();
			kiln.Hot = true;
		}
	}

	[Container(Extends = typeof(AsyncShop))]
	[Singleton<IKiln>(Factory = nameof(KilnAsync))]
	public static partial class AsyncTestShop
	{
		private static async Task<IKiln> KilnAsync([Inherited] Func<Task<IKiln>> production) => await production();
	}

	[Container(Extends = typeof(AsyncShop))]
	[Singleton<IKiln>(Factory = nameof(Kiln))]
	public static partial class FakeKilnShop
	{
		private static FakeKiln Kiln([Inherited] Func<Task<IKiln>> production) => new();
	}

	[Container(Extends = typeof(AsyncShop))]
	[Singleton<IKiln>(Factory = nameof(KilnAsync))]
	public static partial class TaskKilnShop
	{
		private static async Task<IKiln> KilnAsync([Inherited] Task<IKiln> production) => await production;
	}

	[Container(Extends = typeof(AsyncShop))]
	[Singleton<IKiln>(Factory = nameof(KilnAsync))]
	public static partial class LazyTaskKilnShop
	{
		private static async Task<IKiln> KilnAsync([Inherited] Lazy<Task<IKiln>> production) => await production.Value;
	}

	[Container]
	[Singleton<BuildLog>]
	[Singleton<DiskStore, IStore>]
	public static partial class SingletonStoreShop;

	[Container(Extends = typeof(SingletonStoreShop))]
	[ImportService<StoreSettings>]
	[Transient<IStore>(Factory = nameof(Store))]
	[Singleton<Barista>(ConsumerOwned = [typeof(IStore),])]
	public static partial class ConsumerOwnedStoreShop
	{
		private static IStore Store(StoreSettings settings, [Inherited] Func<IStore> production) => settings.Store ?? production();
	}

	[Container(Extends = typeof(SingletonStoreShop))]
	[Scoped<IStore>(Factory = nameof(Store))]
	public static partial class ScopedOverSingletonShop
	{
		private static IStore Store([Inherited] IStore production) => production;
	}

	[Container(Extends = typeof(SingletonStoreShop))]
	[Singleton<StoreCapture>]
	[Singleton<IStore>(Factory = nameof(Store))]
	public static partial class ThrowingStoreShop
	{
		private static IStore Store(StoreCapture capture, [Inherited] IStore production)
		{
			capture.Production = production;
			throw new InvalidOperationException("The double could not be set up.");
		}
	}

	[Container]
	[Singleton<BuildLog>]
	[Singleton<DiskStore, IStore>]
	[Singleton<SpecialStore, IStore>(WhenInjectedInto = typeof(StoreConsumer))]
	[Singleton<StoreConsumer>]
	[Singleton<OtherStoreConsumer>]
	public static partial class ContextShop;

	[Container(Extends = typeof(ContextShop))]
	[Singleton<IStore>(Factory = nameof(Store), WhenInjectedInto = typeof(StoreConsumer))]
	public static partial class ContextTestShop
	{
		private static WrappedStore Store([Inherited] IStore production) => new(production);
	}

	[Container]
	[Import(typeof(SupportFriendFactoryHookModule))]
	public static partial class LampShop;

	[Container(Extends = typeof(LampShop))]
	[Singleton<IFriendFactoryLamp>(Factory = nameof(Lamp))]
	public static partial class LampTestShop
	{
		private static IFriendFactoryLamp Lamp([Inherited] IFriendFactoryLamp production) => production;
	}

	[Container(Extends = typeof(SupportBaseContainer))]
	[Singleton<ISupportBaseClock>(Factory = nameof(Clock))]
	public static partial class SupportClockTestShop
	{
		private static ISupportBaseClock Clock([Inherited] ISupportBaseClock production) => production;
	}

	[Container(Extends = typeof(SupportAsyncHookBaseContainer))]
	[Singleton<ISupportAsyncTerminal>(Factory = nameof(TerminalAsync))]
	public static partial class SupportTerminalTestShop
	{
		private static async Task<ISupportAsyncTerminal> TerminalAsync([Inherited] Task<ISupportAsyncTerminal> production) => await production;
	}
}
