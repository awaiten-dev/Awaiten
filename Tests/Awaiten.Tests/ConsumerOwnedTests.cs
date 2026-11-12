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

	[Container]
	[Transient<Debouncer>]
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
	public static partial class ShopContainer;

	[Container]
	[Import(typeof(CrossAssemblyConsumerOwnedModule))]
	public static partial class ModuleContainer;
}
