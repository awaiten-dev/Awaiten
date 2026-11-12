using System.Collections.Generic;
using Awaiten.Tests.Support;

namespace Awaiten.Tests;

/// <summary>
///     Runtime behavior of <c>ConsumerOwned</c>: a consumer that lists a service owns what its <c>Func</c> factories over
///     that service build, so the container hands each instance over untracked, while every other consumer keeps
///     container-owned instances.
/// </summary>
public partial class ConsumerOwnedTests
{
	[Test]
	public async Task Func_HandsTheInstanceOverUntracked()
	{
		Debouncer owned;
		using (ShopContainer.Root container = new())
		{
			owned = container.Resolve<StorageFactory>().Create();
		}

		await That(owned.Disposed).IsFalse()
			.Because("the consumer owns what its Func builds, so disposing the container leaves it to the consumer");
	}

	[Test]
	public async Task Func_KeepsTheInstancesOfOtherConsumersContainerOwned()
	{
		Debouncer direct;
		using (ShopContainer.Root container = new())
		{
			direct = container.Resolve<SearchBox>().Debouncer;
		}

		await That(direct.Disposed).IsTrue()
			.Because("SearchBox does not list the debouncer, so the container still disposes the one it injects");
	}

	[Test]
	public async Task Func_KeepsScopedDependenciesOwnedByTheirScope()
	{
		Clock clock;
		using (ShopContainer.Root container = new())
		{
			using ShopContainer.Scope scope = container.CreateScope();
			clock = scope.Resolve<ScopedStorageFactory>().Create().Clock;
		}

		await That(clock.Disposed).IsTrue()
			.Because("a scoped dependency of the owned instance belongs to its scope, not to the consumer");
	}

	[Test]
	public async Task FuncWithRuntimeArguments_HandsTheInstanceOverUntracked()
	{
		Cup cup;
		using (ShopContainer.Root container = new())
		{
			cup = container.Resolve<Counter>().Pour("large");
		}

		await That(cup.Size).IsEqualTo("large");
		await That(cup.Disposed).IsFalse()
			.Because("a Func<TArg, T> the consumer owns hands its instances over untracked too");
	}

	[Test]
	public async Task AsyncFunc_HandsTheInstanceOverUntracked()
	{
		Kettle kettle;
		await using (ShopContainer.Root container = new())
		{
			kettle = await container.Resolve<KettleFactory>().CreateAsync();
		}

		await That(kettle.Disposed).IsFalse()
			.Because("a Func<Task<T>> the consumer owns hands its instances over untracked too");
	}

	[Test]
	public async Task DispatchDictionary_HandsEachInstanceOverUntracked()
	{
		IHandler handler;
		using (ShopContainer.Root container = new())
		{
			handler = container.Resolve<Mediator>().Handlers[typeof(Order)]();
		}

		await That(((OrderHandler)handler).Disposed).IsFalse()
			.Because("the entries of a dispatch dictionary the consumer owns hand their instances over untracked");
	}

	[Test]
	public async Task Func_OverADecoratedService_HandsTheOutermostDecoratorOverUntracked()
	{
		LoggingGrinder grinder;
		using (ShopContainer.Root container = new())
		{
			grinder = (LoggingGrinder)container.Resolve<GrinderFactory>().Create();
		}

		await That(grinder.Disposed).IsFalse()
			.Because("the consumer gets the outermost decorator, which is the only disposable its build creates");
	}

	[Test]
	public async Task Func_OfAnInternalConsumerAModuleOfAnotherAssemblyBuilds_HandsTheInstanceOverUntracked()
	{
		OwnedBrewSession session;
		using (ModuleContainer.Root container = new())
		{
			session = container.Resolve<IOwningBarista>().Start();
		}

		await That(session.Disposed).IsFalse()
			.Because("the module builds the internal consumer through a handle, which keeps its ConsumerOwned option");
	}

	[Test]
	public async Task DispatchDictionary_KeepsSharedEntriesContainerOwned()
	{
		Ledger ledger;
		using (ShopContainer.Root container = new())
		{
			ledger = (Ledger)container.Resolve<Mediator>().Handlers[typeof(Refund)]();
		}

		await That(ledger.Disposed).IsTrue()
			.Because("a singleton entry is shared, so the container keeps disposing it even though the consumer owns its transient entries");
	}

	[Test]
	public async Task Func_OverAnAsyncOnlyDisposable_HandsTheInstanceOverUntracked()
	{
		Steamer steamer;
		await using (ShopContainer.Root container = new())
		{
			steamer = container.Resolve<SteamerFactory>().Create();
		}

		await That(steamer.Disposed).IsFalse()
			.Because("the container tracks an async-only disposable behind a wrapper, which __Disown removes too");
	}

	[Test]
	public async Task KeyedFunc_HandsTheInstanceOverUntracked()
	{
		Debouncer debouncer;
		using (ShopContainer.Root container = new())
		{
			debouncer = container.Resolve<KeyedStorageFactory>().Create();
		}

		await That(debouncer.Disposed).IsFalse()
			.Because("a [FromKey] Func reaches the keyed registration, which the consumer owns like the unkeyed one");
	}

	[Test]
	public async Task FuncOfAnInjectedPropertyAndAnActivationHook_HandOverTheirInstancesUntracked()
	{
		Debouncer fromProperty;
		Debouncer fromHook;
		using (ShopContainer.Root container = new())
		{
			HookedStorageFactory factory = container.Resolve<HookedStorageFactory>();
			fromProperty = factory.Debouncers();
			fromHook = factory.FromHook!();
		}

		await That(fromProperty.Disposed).IsFalse()
			.Because("an [Inject] property's Func belongs to the consumer like a constructor parameter's");
		await That(fromHook.Disposed).IsFalse()
			.Because("an activation hook's Func belongs to the consumer like a constructor parameter's");
	}

	[Test]
	public async Task OpenGenericEntry_CoversEachClosing()
	{
		Bag<int> bag;
		using (ShopContainer.Root container = new())
		{
			bag = container.Resolve<BagUser>().Shelf.Create();
		}

		await That(bag.Disposed).IsFalse()
			.Because("typeof(Bag<>) in ConsumerOwned names every closing of the definition");
	}

	public sealed class Debouncer : IDisposable
	{
		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;
	}

	public sealed class StorageFactory(Func<Debouncer> debouncers)
	{
		public Debouncer Create() => debouncers();
	}

	public sealed class SearchBox(Debouncer debouncer)
	{
		public Debouncer Debouncer { get; } = debouncer;
	}

	public sealed class Clock : IDisposable
	{
		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;
	}

	public sealed class Storage(Clock clock) : IDisposable
	{
		public Clock Clock { get; } = clock;

		public void Dispose() { }
	}

	public sealed class ScopedStorageFactory(Func<Storage> storages)
	{
		public Storage Create() => storages();
	}

	public sealed class Cup([Arg] string size) : IDisposable
	{
		public string Size { get; } = size;

		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;
	}

	public sealed class Counter(Func<string, Cup> cups)
	{
		public Cup Pour(string size) => cups(size);
	}

	public sealed class Kettle : IDisposable
	{
		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;
	}

	public sealed class KettleFactory(Func<Task<Kettle>> kettles)
	{
		public Task<Kettle> CreateAsync() => kettles();
	}

	public interface IHandler;

	public interface IHandler<T> : IHandler;

	public sealed class Order;

	public sealed class OrderHandler : IHandler<Order>, IDisposable
	{
		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;
	}

	public sealed class Mediator(IReadOnlyDictionary<Type, Func<IHandler>> handlers)
	{
		public IReadOnlyDictionary<Type, Func<IHandler>> Handlers { get; } = handlers;
	}

	public interface IGrinder;

	public sealed class Grinder : IGrinder;

	public sealed class LoggingGrinder(IGrinder inner) : IGrinder, IDisposable
	{
		public IGrinder Inner { get; } = inner;

		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;
	}

	public sealed class GrinderFactory(Func<IGrinder> grinders)
	{
		public IGrinder Create() => grinders();
	}

	public sealed class Refund;

	public sealed class Ledger : IHandler<Refund>, IDisposable
	{
		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;
	}

	public sealed class Steamer : IAsyncDisposable
	{
		public bool Disposed { get; private set; }

		public ValueTask DisposeAsync()
		{
			Disposed = true;
			return default;
		}
	}

	public sealed class SteamerFactory(Func<Steamer> steamers)
	{
		public Steamer Create() => steamers();
	}

	public sealed class KeyedStorageFactory([FromKey("night")] Func<Debouncer> debouncers)
	{
		public Debouncer Create() => debouncers();
	}

	public sealed class HookedStorageFactory
	{
		[Inject]
		public Func<Debouncer> Debouncers { get; set; } = null!;

		public Func<Debouncer>? FromHook { get; set; }
	}

	public sealed class Bag<T> : IDisposable
	{
		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;
	}

	public sealed class BagShelf<T>(Func<Bag<T>> bags)
	{
		public Bag<T> Create() => bags();
	}

	public sealed class BagUser(BagShelf<int> shelf)
	{
		public BagShelf<int> Shelf { get; } = shelf;
	}

	[Container]
	[Transient<Debouncer>]
	[Transient<Debouncer>(Key = "night")]
	[Singleton<KeyedStorageFactory>(ConsumerOwned = [typeof(Debouncer),])]
	[Singleton<HookedStorageFactory>(OnActivated = nameof(Activate), ConsumerOwned = [typeof(Debouncer),])]
	[Singleton<Ledger, IHandler<Refund>>]
	[Transient<Steamer>]
	[Singleton<SteamerFactory>(ConsumerOwned = [typeof(Steamer),])]
	[Transient(typeof(Bag<>))]
	[Singleton(typeof(BagShelf<>), ConsumerOwned = [typeof(Bag<>),])]
	[Transient<BagUser>]
	[Singleton<StorageFactory>(ConsumerOwned = [typeof(Debouncer),])]
	[Transient<SearchBox>]
	[Scoped<Clock>]
	[Transient<Storage>]
	[Scoped<ScopedStorageFactory>(ConsumerOwned = [typeof(Storage),])]
	[Transient<Cup>]
	[Singleton<Counter>(ConsumerOwned = [typeof(Cup),])]
	[Transient<Kettle>]
	[Singleton<KettleFactory>(ConsumerOwned = [typeof(Kettle),])]
	[Transient<OrderHandler, IHandler<Order>>]
	[Singleton<Mediator>(ConsumerOwned = [typeof(IHandler),])]
	[Transient<Grinder, IGrinder>]
	[Decorate<LoggingGrinder, IGrinder>]
	[Singleton<GrinderFactory>(ConsumerOwned = [typeof(IGrinder),])]
	public static partial class ShopContainer
	{
		private static void Activate(HookedStorageFactory factory, Func<Debouncer> debouncers) => factory.FromHook = debouncers;
	}

	[Container]
	[Import(typeof(CrossAssemblyConsumerOwnedModule))]
	public static partial class ModuleContainer;
}
