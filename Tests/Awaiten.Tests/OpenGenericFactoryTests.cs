using System.Collections.Generic;
using System.Linq;
using Awaiten.Tests.ContainerSupport;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of open generic registrations, decorators and composites a module or a base container builds
///     through a generated generic factory for a container that cannot construct the open type itself.
/// </summary>
public sealed partial class OpenGenericFactoryTests
{
	public sealed class Order;

	public sealed class Invoice;

	public sealed class OpenClock : IOpenClock;

	public sealed class OpenCheckout(IOpenStore<Order> orders, IOpenStore<Invoice> invoices, IOpenHandler<Order> handlers)
	{
		public IOpenStore<Order> Orders => orders;

		public IOpenStore<Invoice> Invoices => invoices;

		public IOpenHandler<Order> Handlers => handlers;
	}

	public sealed class AuditedOpenStore<T>(IOpenStore<T> inner) : IOpenStore<T>
		where T : class
	{
		public IOpenStore<T>? Inner => inner;

		public IOpenClock Clock => inner.Clock;

		public bool Initialized => inner.Initialized;

		public bool Disposed => inner.Disposed;
	}

	public sealed class ModuleInAnotherAssembly
	{
		[Test]
		public async Task EachClosing_IsBuiltThroughTheGenericFactory()
		{
			await using OpenModuleContainer.Root container = new();
			await using IAwaitenScope scope = await container.CreateScopeAsync();

			OpenCheckout checkout = await scope.ResolveAsync<OpenCheckout>();

			await That(Innermost(checkout.Orders).GetType().Name).IsEqualTo("OpenStore`1");
			await That(Innermost(checkout.Invoices).GetType().Name).IsEqualTo("OpenStore`1");
			await That(checkout.Orders.Clock).IsSameAs(container.Resolve<IOpenClock>())
				.Because("the factory's parameter resolves from the consuming container's graph");
		}

		[Test]
		public async Task AScopedClosing_IsSharedWithinAScopeOnly()
		{
			await using OpenModuleContainer.Root container = new();
			await using IAwaitenScope first = await container.CreateScopeAsync();
			await using IAwaitenScope second = await container.CreateScopeAsync();

			IOpenStore<Order> store = await first.ResolveAsync<IOpenStore<Order>>();

			await That(await first.ResolveAsync<IOpenStore<Order>>()).IsSameAs(store);
			await That(await second.ResolveAsync<IOpenStore<Order>>()).IsNotSameAs(store)
				.Because("each closing keeps the lifetime of the open registration");
		}

		[Test]
		public async Task TheModulesConvention_InitializesEachClosing()
		{
			await using OpenModuleContainer.Root container = new();
			await using IAwaitenScope scope = await container.CreateScopeAsync();

			IOpenStore<Order> store = await scope.ResolveAsync<IOpenStore<Order>>();

			await That(store.Initialized).IsTrue()
				.Because("the closed implementation's traits are read off its metadata, so the module's convention runs although the factory returns the service");
			await That(container.GetResolvability<IOpenStore<Order>>().Status).IsEqualTo(AwaitenResolvabilityStatus.AsyncOnly);
		}

		[Test]
		public async Task TheContainer_DisposesEachClosing()
		{
			await using OpenModuleContainer.Root container = new();
			IOpenStore<Order> store;
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				store = await scope.ResolveAsync<IOpenStore<Order>>();
				await That(store.Disposed).IsFalse();
			}

			await That(store.Disposed).IsTrue()
				.Because("the closed implementation is disposable, which the container reads off its metadata");
		}

		[Test]
		public async Task TheModulesDecorator_WrapsInsideTheContainersOwn()
		{
			await using OpenModuleContainer.Root container = new();
			await using IAwaitenScope scope = await container.CreateScopeAsync();

			IOpenStore<Order> store = await scope.ResolveAsync<IOpenStore<Order>>();

			await That(store).IsExactly<AuditedOpenStore<Order>>()
				.Because("the container's decorator has the higher order");
			await That(store.Inner!.GetType().Name).IsEqualTo("LoggingOpenStore`1");
			await That(store.Inner!.Inner!.GetType().Name).IsEqualTo("OpenStore`1");
		}

		[Test]
		public async Task TheModulesComposite_FrontsEveryClosedHandler()
		{
			await using OpenModuleContainer.Root container = new();

			IOpenHandler<Order> handler = container.Resolve<IOpenHandler<Order>>();

			await That(handler.GetType().Name).IsEqualTo("AllOpenHandlers`1");
			await That(handler.Count).IsEqualTo(2);
		}

		[Test]
		public async Task TheModulesOpenDecorator_DecoratesAClosingUnderAnInternalType()
		{
			await using OpenModuleContainer.Root container = new();

			await That(container.Resolve<IOpenLedgerReport>().Chain).IsEqualTo("AuditedOpenLedger`1>EntryLedger")
				.Because("the module closes its open decorator onto its internal closing and builds it into the closing's handle");
		}

		[Test]
		public async Task AClosedAndAnOpenRegistrationOfOneType_ShareOneInstance()
		{
			await using SharedOpenContainer.Root container = new();

			SharedOpenContainer.Consumer consumer = container.Resolve<SharedOpenContainer.Consumer>();

			await That(consumer.Reader).IsSameAs((object)consumer.Repository)
				.Because("both registrations build the one singleton through the factory whose return type serves both services");
		}

		private static IOpenStore<T> Innermost<T>(IOpenStore<T> store)
			where T : class
			=> store.Inner is { } inner ? Innermost(inner) : store;
	}

	public sealed class FriendModule
	{
		[Test]
		public async Task APrivateImplementation_IsBuiltAndDisposedThroughTheGenericFactory()
		{
			await using FriendOpenContainer.Root container = new();
			IFriendOpenStore<Order> store;
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				store = scope.Resolve<IFriendOpenStore<Order>>();
				await That(store.Clock).IsSameAs(container.Resolve<IOpenClock>());
				await That(scope.Resolve<IFriendOpenStore<Order>>()).IsSameAs(store);
			}

			await That(store.Disposed).IsTrue();
		}
	}

	public sealed class ModuleInTheSameAssembly
	{
		[Test]
		public async Task PrivateTypes_AreBuiltThroughTheGenericFactories()
		{
			await using SameAssemblyOpenContainer.Root container = new();
			IRecord<Order> record;
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				record = scope.Resolve<IRecord<Order>>();
				await That(record.GetType().Name).IsEqualTo("Tracked`1");
				await That(record.Inner!.GetType().Name).IsEqualTo("Record`1");
				await That(scope.Resolve<IRecord<Order>>()).IsSameAs(record);
				await That(scope.Resolve<IRecord<Invoice>>()).IsNotSameAs((object)record);
			}

			await That(record.Disposed).IsTrue();
			await That(container.Resolve<IRecordSink<Order>>().Count).IsEqualTo(2)
				.Because("the private composite fronts both sinks");
		}

		[Test]
		public async Task AClosedAndAnOpenRegistrationOfOnePrivateType_ShareOneInstance()
		{
			await using SharedRecordContainer.Root container = new();

			SharedRecordContainer.Consumer consumer = container.Resolve<SharedRecordContainer.Consumer>();

			await That(consumer.Sink).IsSameAs((object)consumer.Record);
		}

		[Test]
		public async Task AClosingOfAPrivateDecorator_IsBuiltThroughItsFactory()
		{
			await using ClosedDecoratorContainer.Root container = new();

			IRecord<Order> record = container.Resolve<IRecord<Order>>();

			await That(record.GetType().Name).IsEqualTo("Wrapped`1");
			await That(record.Inner).IsExactly<PlainRecord<Order>>();
		}
	}

	public sealed class BaseInAnotherAssembly
	{
		[Test]
		public async Task PrivateTypes_AreBuiltThroughTheBasesGenericFactories()
		{
			await using SupportOpenTestContainer.Root container = new();
			IBaseOpenStore<BaseOpenOrder> store;
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				store = scope.Resolve<BaseOpenCheckout>().Orders;
				await That(store.GetType().Name).IsEqualTo("Guard`1");
				await That(store.Inner!.GetType().Name).IsEqualTo("Store`1");
				await That(store.Clock).IsSameAs(container.Resolve<IOpenClock>())
					.Because("the base's generic factory takes the property of its [InjectProperty] entry as a parameter");
			}

			await That(store.Disposed).IsTrue();
		}
	}

	public sealed class BaseInTheSameAssembly
	{
		[Test]
		public async Task PrivateTypes_AreBuiltThroughTheBasesGenericFactories()
		{
			await using ExtendedRecordShop.Root container = new();
			IRecord<Order> record;
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				record = scope.Resolve<IRecord<Order>>();
				await That(record.GetType().Name).IsEqualTo("Tracked`1");
				await That(record.Inner!.GetType().Name).IsEqualTo("Record`1");
			}

			await That(record.Disposed).IsTrue();
			await That(container.Resolve<IRecordSink<Order>>().Count).IsEqualTo(2);
		}

		[Test]
		public async Task TheBasesInjectPropertyEntry_IsFilledThroughTheGenericFactory()
		{
			await using ExtendedClockedShop.Root container = new();

			IClocked<Order> clocked = container.Resolve<ClockedConsumer>().Clocked;

			await That(clocked.Clock).IsSameAs(container.Resolve<IOpenClock>());
			await That(clocked.Later).IsSameAs(container.Resolve<IOpenClock>())
				.Because("the extending container calls the setter the base generates next to its generic factory");
		}
	}

	public sealed class Hooks
	{
		[Test]
		public async Task AModuleInAnotherAssembly_RunsTheHooksOfEachClosingThroughTheGenericWrappers()
		{
			OpenHookLog log;
			await using (HookedCacheContainer.Root container = new())
			{
				log = container.Resolve<OpenHookLog>();
				await using (IAwaitenScope scope = await container.CreateScopeAsync())
				{
					scope.Resolve<CacheConsumer>();
					await That(log.Entries).IsEqualTo(["activated Order", "activated Invoice",]).InAnyOrder()
						.Because("the generic activation hook is bound to each closing's type argument");
				}

				await That(log.Entries.Count(entry => entry == "released")).IsEqualTo(2)
					.Because("the release hook runs once per scoped closing when its scope ends");
			}
		}

		[Test]
		public async Task AFriendModule_RunsThePrivateHooksThroughTheGenericWrappers()
		{
			await using FriendHookedCacheContainer.Root container = new();

			await That(await ResolveInScope(container)).IsEqualTo(PrivateHookCalls).InAnyOrder();
		}

		[Test]
		public async Task AModuleInTheSameAssembly_RunsThePrivateHooksThroughTheGenericWrappers()
		{
			await using SameAssemblyHookedCacheContainer.Root container = new();

			await That(await ResolveInScope(container)).IsEqualTo(PrivateHookCalls).InAnyOrder();
		}

		[Test]
		public async Task ABaseInAnotherAssembly_RunsThePrivateHooksThroughTheGenericWrappers()
		{
			await using HookedCacheTestContainer.Root container = new();

			await That(await ResolveInScope(container)).IsEqualTo(PrivateHookCalls).InAnyOrder();
		}

		[Test]
		public async Task ABaseInTheSameAssembly_RunsThePrivateHooksThroughTheGenericWrappers()
		{
			await using ExtendedHookedCacheShop.Root container = new();

			await That(await ResolveInScope(container)).IsEqualTo(PrivateHookCalls).InAnyOrder();
		}

		[Test]
		public async Task AModuleInAnotherAssembly_AwaitsTheAsynchronousHooksOfEachClosing()
		{
			await using AsyncHookedCacheContainer.Root container = new();

			await That(await ResolveInScopeAsync(container)).IsEqualTo(PrivateHookCalls).InAnyOrder()
				.Because("the Task activation hook and the ValueTask release hook run through the generic wrappers and are awaited");
		}

		[Test]
		public async Task AModuleInTheSameAssembly_AwaitsTheAsynchronousHooksOfEachClosing()
		{
			await using SameAssemblyAsyncHookedCacheContainer.Root container = new();

			await That(await ResolveInScopeAsync(container)).IsEqualTo(PrivateHookCalls).InAnyOrder();
		}

		private static readonly string[] PrivateHookCalls = ["activated Order", "activated Invoice", "released Order", "released Invoice",];

		private static async Task<List<string>> ResolveInScope(IAwaitenRoot container)
		{
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				scope.Resolve<CacheConsumer>();
			}

			return container.Resolve<OpenHookLog>().Entries;
		}

		private static async Task<List<string>> ResolveInScopeAsync(IAwaitenRoot container)
		{
			await using (IAwaitenScope scope = await container.CreateScopeAsync())
			{
				await scope.ResolveAsync<CacheConsumer>();
			}

			return container.Resolve<OpenHookLog>().Entries;
		}
	}

	public sealed class CacheConsumer(IHookedOpenCache<Order> orders, IHookedOpenCache<Invoice> invoices)
	{
		public IHookedOpenCache<Order> Orders => orders;

		public IHookedOpenCache<Invoice> Invoices => invoices;
	}

	[Container]
	[Import(typeof(HookedOpenGenericModule))]
	[Scoped<CacheConsumer>]
	public static partial class HookedCacheContainer;

	[Container]
	[Import(typeof(FriendHookedOpenGenericModule))]
	[Scoped<CacheConsumer>]
	public static partial class FriendHookedCacheContainer;

	[Module]
	[Singleton<OpenHookLog>]
	[Scoped(typeof(SameAssemblyHookedCacheModule.Cache<>), typeof(IHookedOpenCache<>), OnActivated = nameof(Activate), OnRelease = nameof(Release))]
	public static partial class SameAssemblyHookedCacheModule
	{
		private sealed class Cache<T> : IHookedOpenCache<T>
			where T : class;

		private static void Activate<T>(Cache<T> cache, OpenHookLog log)
			where T : class
			=> log.Entries.Add($"activated {typeof(T).Name}");

		private static void Release<T>(Cache<T> cache, OpenHookLog log)
			where T : class
			=> log.Entries.Add($"released {typeof(T).Name}");
	}

	[Container]
	[Import(typeof(SameAssemblyHookedCacheModule))]
	[Scoped<CacheConsumer>]
	public static partial class SameAssemblyHookedCacheContainer;

	[Container]
	[Import(typeof(AsyncHookedOpenGenericModule))]
	[Scoped<CacheConsumer>]
	public static partial class AsyncHookedCacheContainer;

	[Module]
	[Singleton<OpenHookLog>]
	[Scoped(typeof(SameAssemblyAsyncHookedCacheModule.Cache<>), typeof(IHookedOpenCache<>), OnActivated = nameof(Activate), OnRelease = nameof(Release))]
	public static partial class SameAssemblyAsyncHookedCacheModule
	{
		private sealed class Cache<T> : IHookedOpenCache<T>
			where T : class;

		private static async Task Activate<T>(Cache<T> cache, OpenHookLog log)
			where T : class
		{
			await Task.Yield();
			log.Entries.Add($"activated {typeof(T).Name}");
		}

		private static ValueTask Release<T>(Cache<T> cache, OpenHookLog log)
			where T : class
		{
			log.Entries.Add($"released {typeof(T).Name}");
			return default;
		}
	}

	[Container]
	[Import(typeof(SameAssemblyAsyncHookedCacheModule))]
	[Scoped<CacheConsumer>]
	public static partial class SameAssemblyAsyncHookedCacheContainer;

	[Container(Extends = typeof(HookedOpenBaseContainer))]
	[Scoped<CacheConsumer>]
	public static partial class HookedCacheTestContainer;

	[Container]
	[Singleton<OpenHookLog>]
	[Scoped(typeof(HookedCacheShop.Cache<>), typeof(IHookedOpenCache<>), OnActivated = nameof(Activate), OnRelease = nameof(Release))]
	[Scoped<CacheConsumer>]
	public static partial class HookedCacheShop
	{
		private sealed class Cache<T> : IHookedOpenCache<T>
			where T : class;

		private static void Activate<T>(Cache<T> cache, OpenHookLog log)
			where T : class
			=> log.Entries.Add($"activated {typeof(T).Name}");

		private static void Release<T>(Cache<T> cache, OpenHookLog log)
			where T : class
			=> log.Entries.Add($"released {typeof(T).Name}");
	}

	[Container(Extends = typeof(HookedCacheShop))]
	public static partial class ExtendedHookedCacheShop;

	public sealed class ContainerSideInjectProperty
	{
		[Test]
		public async Task AModuleInAnotherAssembly_FillsTheEntryOnTheInstanceTheGenericFactoryReturns()
		{
			await using InjectedJournalContainer.Root container = new();

			IInjectedOpenJournal<Order> journal = container.Resolve<JournalConsumer>().Orders;

			await That(journal.Clock).IsSameAs(container.Resolve<IOpenClock>())
				.Because("the generic factory sets the module's internal [Inject] property");
			await That(journal.Audit).IsSameAs(container.Resolve<IOpenClock>())
				.Because("the container sets its own entry on the instance the factory returned");
			await That(container.Resolve<JournalConsumer>().Invoices.Audit).IsSameAs(container.Resolve<IOpenClock>())
				.Because("the open entry applies to every closing");
		}

		[Test]
		public async Task AFriendModule_FillsTheEntryItself()
		{
			await using FriendInjectedJournalContainer.Root container = new();

			IInjectedOpenJournal<Order> journal = container.Resolve<JournalConsumer>().Orders;

			await That(journal.Clock).IsSameAs(container.Resolve<IOpenClock>());
			await That(journal.Audit).IsSameAs(container.Resolve<IOpenClock>());
		}

		[Test]
		public async Task AModuleInTheSameAssembly_FillsTheEntryItself()
		{
			await using SameAssemblyInjectedJournalContainer.Root container = new();

			IInjectedOpenJournal<Order> journal = container.Resolve<JournalConsumer>().Orders;

			await That(journal.Clock).IsSameAs(container.Resolve<IOpenClock>());
			await That(journal.Audit).IsSameAs(container.Resolve<IOpenClock>());
		}
	}

	public sealed class JournalConsumer(IInjectedOpenJournal<Order> orders, IInjectedOpenJournal<Invoice> invoices)
	{
		public IInjectedOpenJournal<Order> Orders => orders;

		public IInjectedOpenJournal<Invoice> Invoices => invoices;
	}

	public sealed class SameAssemblyInjectedJournal<T> : IInjectedOpenJournal<T>
	{
		[Inject]
		internal IOpenClock Injected { get; set; } = null!;

		public IOpenClock Clock => Injected;

		public IOpenClock? Audit { get; set; }
	}

	[Module]
	[Singleton(typeof(SameAssemblyInjectedJournal<>), typeof(IInjectedOpenJournal<>))]
	public static partial class SameAssemblyInjectedJournalModule;

	[Container]
	[Import(typeof(InjectedOpenJournalModule))]
	[Singleton<OpenClock, IOpenClock>]
	[InjectProperty(typeof(InjectedOpenJournal<>), nameof(IInjectedOpenJournal<Order>.Audit))]
	[Transient<JournalConsumer>]
	public static partial class InjectedJournalContainer;

	[Container]
	[Import(typeof(FriendInjectedOpenJournalModule))]
	[Singleton<OpenClock, IOpenClock>]
	[InjectProperty(typeof(FriendInjectedOpenJournal<>), nameof(IInjectedOpenJournal<Order>.Audit))]
	[Transient<JournalConsumer>]
	public static partial class FriendInjectedJournalContainer;

	[Container]
	[Import(typeof(SameAssemblyInjectedJournalModule))]
	[Singleton<OpenClock, IOpenClock>]
	[InjectProperty(typeof(SameAssemblyInjectedJournal<>), nameof(IInjectedOpenJournal<Order>.Audit))]
	[Transient<JournalConsumer>]
	public static partial class SameAssemblyInjectedJournalContainer;

	public sealed class ClockedConsumer(IClocked<Order> clocked)
	{
		public IClocked<Order> Clocked => clocked;
	}

	public interface IClocked<T>
	{
		IOpenClock? Clock { get; }

		IOpenClock? Later { get; }
	}

	[Container]
	[Singleton<OpenClock, IOpenClock>]
	[Singleton(typeof(ClockedShop.Clocked<>), typeof(IClocked<>))]
	[InjectProperty(typeof(ClockedShop.Clocked<>), nameof(IClocked<Order>.Clock))]
	[Transient<ClockedConsumer>]
	public static partial class ClockedShop
	{
		private sealed class Clocked<T> : IClocked<T>
		{
			public IOpenClock? Clock { get; set; }

			[Inject(Deferred = true)]
			public IOpenClock? Later { get; set; }
		}
	}

	[Container(Extends = typeof(ClockedShop))]
	public static partial class ExtendedClockedShop;

	public interface IRecord<T>
		where T : class
	{
		IRecord<T>? Inner { get; }

		bool Disposed { get; }
	}

	public interface IRecordSink<T>
	{
		int Count { get; }
	}

	public sealed class FirstSink<T> : IRecordSink<T>
	{
		public int Count => 1;
	}

	public sealed class SecondSink<T> : IRecordSink<T>
	{
		public int Count => 1;
	}

	public sealed class RecordConsumer(IRecord<Order> orders, IRecord<Invoice> invoices, IRecordSink<Order> sinks)
	{
		public IRecord<Order> Orders => orders;

		public IRecord<Invoice> Invoices => invoices;

		public IRecordSink<Order> Sinks => sinks;
	}

	[Container]
	[Import(typeof(OpenGenericModule))]
	[Singleton<OpenClock, IOpenClock>]
	[Scoped<OpenCheckout>]
	[Decorate(typeof(AuditedOpenStore<>), typeof(IOpenStore<>), Order = 1)]
	public static partial class OpenModuleContainer;

	[Container]
	[Import(typeof(SharedOpenGenericModule))]
	[Transient<Consumer>]
	public static partial class SharedOpenContainer
	{
		public sealed class Consumer(ISharedOpenReader<SharedOpenEntity> reader, ISharedOpenRepository<SharedOpenEntity> repository)
		{
			public ISharedOpenReader<SharedOpenEntity> Reader => reader;

			public ISharedOpenRepository<SharedOpenEntity> Repository => repository;
		}
	}

	[Container]
	[Import(typeof(FriendOpenGenericModule))]
	[Singleton<OpenClock, IOpenClock>]
	[Scoped<FriendOpenCheckout>]
	public static partial class FriendOpenContainer
	{
		public sealed class FriendOpenCheckout(IFriendOpenStore<Order> orders)
		{
			public IFriendOpenStore<Order> Orders => orders;
		}
	}

	[Module]
	[Scoped(typeof(RecordModule.Record<>), typeof(IRecord<>))]
	[Decorate(typeof(RecordModule.Tracked<>), typeof(IRecord<>))]
	[Transient(typeof(FirstSink<>), typeof(IRecordSink<>))]
	[Transient(typeof(SecondSink<>), typeof(IRecordSink<>))]
	[Composite(typeof(RecordModule.AllSinks<>), typeof(IRecordSink<>))]
	public static partial class RecordModule
	{
		private sealed class Record<T> : IRecord<T>, System.IDisposable
			where T : class
		{
			public IRecord<T>? Inner => null;

			public bool Disposed { get; private set; }

			public void Dispose() => Disposed = true;
		}

		private sealed class Tracked<T>(IRecord<T> inner) : IRecord<T>
			where T : class
		{
			public IRecord<T>? Inner => inner;

			public bool Disposed => inner.Disposed;
		}

		private sealed class AllSinks<T>(IEnumerable<IRecordSink<T>> sinks) : IRecordSink<T>
		{
			public int Count => sinks.Sum(sink => sink.Count);
		}
	}

	[Container]
	[Import(typeof(RecordModule))]
	[Scoped<RecordConsumer>]
	public static partial class SameAssemblyOpenContainer;

	public sealed class PlainRecord<T> : IRecord<T>
		where T : class
	{
		public IRecord<T>? Inner => null;

		public bool Disposed => false;
	}

	[Module]
	[Singleton<PlainRecord<Order>, IRecord<Order>>]
	[Decorate<ClosedDecoratorModule.Wrapped<Order>, IRecord<Order>>]
	public static partial class ClosedDecoratorModule
	{
		private sealed class Wrapped<T>(IRecord<T> inner) : IRecord<T>
			where T : class
		{
			public IRecord<T>? Inner => inner;

			public bool Disposed => inner.Disposed;
		}
	}

	[Container]
	[Import(typeof(ClosedDecoratorModule))]
	public static partial class ClosedDecoratorContainer;

	public interface ISharedRecord<T> : IRecordSink<T>;

	[Module]
	[Singleton<SharedRecordModule.Shared<Order>, IRecordSink<Order>>]
	[Singleton(typeof(SharedRecordModule.Shared<>), typeof(ISharedRecord<>))]
	public static partial class SharedRecordModule
	{
		private sealed class Shared<T> : ISharedRecord<T>
		{
			public int Count => 1;
		}
	}

	[Container]
	[Import(typeof(SharedRecordModule))]
	[Transient<Consumer>]
	public static partial class SharedRecordContainer
	{
		public sealed class Consumer(IRecordSink<Order> sink, ISharedRecord<Order> record)
		{
			public IRecordSink<Order> Sink => sink;

			public ISharedRecord<Order> Record => record;
		}
	}

	[Container]
	[Scoped(typeof(RecordShop.Record<>), typeof(IRecord<>))]
	[Decorate(typeof(RecordShop.Tracked<>), typeof(IRecord<>))]
	[Transient(typeof(FirstSink<>), typeof(IRecordSink<>))]
	[Transient(typeof(SecondSink<>), typeof(IRecordSink<>))]
	[Composite(typeof(RecordShop.AllSinks<>), typeof(IRecordSink<>))]
	[Scoped<RecordConsumer>]
	public static partial class RecordShop
	{
		private sealed class Record<T> : IRecord<T>, System.IDisposable
			where T : class
		{
			public IRecord<T>? Inner => null;

			public bool Disposed { get; private set; }

			public void Dispose() => Disposed = true;
		}

		private sealed class Tracked<T>(IRecord<T> inner) : IRecord<T>
			where T : class
		{
			public IRecord<T>? Inner => inner;

			public bool Disposed => inner.Disposed;
		}

		private sealed class AllSinks<T>(IEnumerable<IRecordSink<T>> sinks) : IRecordSink<T>
		{
			public int Count => sinks.Sum(sink => sink.Count);
		}
	}

	[Container(Extends = typeof(RecordShop))]
	public static partial class ExtendedRecordShop;

	[Container(Extends = typeof(SupportOpenBaseContainer))]
	public static partial class SupportOpenTestContainer;
}
