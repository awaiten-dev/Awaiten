using System.Collections.Generic;
using System.Threading;

namespace Awaiten.Tests;

/// <summary>
///     The async relationship shapes resolved by type: <c>Task&lt;T&gt;</c>, <c>Func&lt;Task&lt;T&gt;&gt;</c>,
///     <c>Lazy&lt;Task&lt;T&gt;&gt;</c>, <c>Task&lt;Owned&lt;T&gt;&gt;</c>, <c>Func&lt;Task&lt;Owned&lt;T&gt;&gt;&gt;</c> and the
///     <c>Func&lt;TArg…, …&gt;</c> forms over a parameterized service, from the root and from a child scope, as injection
///     hands them out. They are served over the services that need them: an async-initialized or parameterized one.
/// </summary>
public sealed partial class TaskRelationshipDispatchTests
{
	public interface IStore
	{
		bool Initialized { get; }
	}

	public interface ILabel
	{
		Type? RequestingType { get; }
	}

	public sealed class Store : IStore, IInitializable
	{
		public bool Initialized { get; private set; }

		public async Task InitializeAsync(CancellationToken cancellationToken)
		{
			await Task.Yield();
			Initialized = true;
		}
	}

	public sealed class Plain;

	public sealed class Brew([Arg] int size) : IInitializable
	{
		public int Size => size;

		public bool Initialized { get; private set; }

		public async Task InitializeAsync(CancellationToken cancellationToken)
		{
			await Task.Yield();
			Initialized = true;
		}
	}

	public sealed class Mug([Arg] int size) : IDisposable
	{
		public int Size => size;

		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;
	}

	public sealed class Kettle : IInitializable, IDisposable
	{
		public bool Initialized { get; private set; }

		public bool Disposed { get; private set; }

		public async Task InitializeAsync(CancellationToken cancellationToken)
		{
			await Task.Yield();
			Initialized = true;
		}

		public void Dispose() => Disposed = true;
	}

	public sealed class Label(Type? requestingType) : ILabel
	{
		public Type? RequestingType => requestingType;
	}

	public sealed class AsyncService
	{
		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task Task_HandsOutTheInitializedSingleton(bool fromScope)
		{
			await using Shop.Root shop = new();
			await using Shop.Scope scope = shop.CreateScope();
			IAwaitenScope resolver = fromScope ? scope : shop;

			IStore store = await (Task<IStore>)resolver.Resolve(typeof(Task<IStore>));

			await That(store.Initialized).IsTrue()
				.Because("the task awaits the async initializer before it completes");
			await That(store).IsSameAs(await shop.ResolveAsync<IStore>())
				.Because("the task hands out the one singleton the typed async resolution hands out");
		}

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task FuncAndLazyOfTask_HandOutTheInitializedSingleton(bool fromScope)
		{
			await using Shop.Root shop = new();
			await using Shop.Scope scope = shop.CreateScope();
			IAwaitenScope resolver = fromScope ? scope : shop;
			IStore expected = await shop.ResolveAsync<IStore>();

			Func<Task<IStore>> factory = (Func<Task<IStore>>)resolver.Resolve(typeof(Func<Task<IStore>>));
			Lazy<Task<IStore>> lazy = (Lazy<Task<IStore>>)resolver.Resolve(typeof(Lazy<Task<IStore>>));

			await That(await factory()).IsSameAs(expected);
			await That(await lazy.Value).IsSameAs(expected);
		}

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task OwnedForms_HandOutTheInitializedSingletonInAHandle(bool fromScope)
		{
			await using Shop.Root shop = new();
			await using Shop.Scope scope = shop.CreateScope();
			IAwaitenScope resolver = fromScope ? scope : shop;
			IStore expected = await shop.ResolveAsync<IStore>();

			await using Owned<IStore> owned = await (Task<Owned<IStore>>)resolver.Resolve(typeof(Task<Owned<IStore>>));
			Func<Task<Owned<IStore>>> factory = (Func<Task<Owned<IStore>>>)resolver.Resolve(typeof(Func<Task<Owned<IStore>>>));
			await using Owned<IStore> produced = await factory();

			await That(owned.Value).IsSameAs(expected)
				.Because("the singleton is shared with the throwaway scope of the handle");
			await That(produced.Value).IsSameAs(expected);
		}

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task ResolveAsyncOfOwned_HandsOutTheInitializedServiceInAHandle(bool fromScope)
		{
			await using Shop.Root shop = new();
			await using Shop.Scope scope = shop.CreateScope();
			IAwaitenScope resolver = fromScope ? scope : shop;

			await using Owned<Kettle> owned = (Owned<Kettle>)await resolver.ResolveAsync(typeof(Owned<Kettle>));

			await That(owned.Value.Initialized).IsTrue()
				.Because("ResolveAsync awaits the initializer of the service inside the handle");
			await owned.DisposeAsync();
			await That(owned.Value.Disposed).IsTrue()
				.Because("the handle owns the throwaway scope that tracks the transient");
		}

		[Test]
		public async Task ResolveOfASynchronousShape_ThrowsGuidanceTowardsTheTaskShape()
		{
			await using Shop.Root shop = new();

			await That(() => shop.Resolve(typeof(Func<IStore>))).Throws<InvalidOperationException>()
				.WithMessage("*Func<*IStore>*asynchronous initialization*Func<System.Threading.Tasks.Task<*IStore>>*").AsWildcard()
				.Because("a synchronous Func cannot await the initializer, so the guidance names the shape that can");
			await That(() => shop.Resolve(typeof(Lazy<IStore>))).Throws<InvalidOperationException>()
				.WithMessage("*Lazy<System.Threading.Tasks.Task<*IStore>>*").AsWildcard();
			await That(() => shop.Resolve(typeof(Owned<IStore>))).Throws<InvalidOperationException>()
				.WithMessage("*Task<Awaiten.Owned<*IStore>>*").AsWildcard();
			await That(() => shop.Resolve(typeof(Func<Owned<IStore>>))).Throws<InvalidOperationException>()
				.WithMessage("*Func<System.Threading.Tasks.Task<Awaiten.Owned<*IStore>>>*").AsWildcard();
		}

		[Test]
		[Arguments(typeof(Lazy<Task<Owned<IStore>>>))]
		[Arguments(typeof(Func<Func<Task<IStore>>>))]
		[Arguments(typeof(Task<Owned<ILabel>>))]
		public async Task ResolveOfAnUnservedShape_DoesNotClaimTheServiceNeedsNoAsyncInitialization(Type shape)
		{
			await using Shop.Root shop = new();

			await That(() => shop.Resolve(shape)).Throws<InvalidOperationException>()
				.WithMessage("No registration for type*on this container.").AsWildcard()
				.Because("the service needs asynchronous initialization, so the shape is missing for another reason");
		}

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task TaskOfAServiceRegisteredAsACollection_HandsOutTheRegisteredService(bool fromScope)
		{
			await using Pantry.Root pantry = new();
			await using Pantry.Scope scope = pantry.CreateScope();
			IAwaitenScope resolver = fromScope ? scope : pantry;

			IReadOnlyList<Plain> beans = await (Task<IReadOnlyList<Plain>>)resolver.Resolve(typeof(Task<IReadOnlyList<Plain>>));

			await That(beans).IsSameAs(await pantry.ResolveAsync<IReadOnlyList<Plain>>())
				.Because("the registration suppresses the synthesized collection, so the task is over the registered service");
			await That(pantry.GetResolvability(typeof(Task<IReadOnlyList<Plain>>), null).Status).IsEqualTo(AwaitenResolvabilityStatus.Resolvable);
		}
	}

	public sealed class SynchronousService
	{
		[Test]
		[Arguments(typeof(Task<Plain>))]
		[Arguments(typeof(Func<Task<Plain>>))]
		[Arguments(typeof(Lazy<Task<Plain>>))]
		[Arguments(typeof(Task<Owned<Plain>>))]
		[Arguments(typeof(Func<Task<Owned<Plain>>>))]
		public async Task TaskShape_IsNotServedAndTheGuidanceNamesTheService(Type shape)
		{
			await using Shop.Root shop = new();

			await That(shop.TryResolve(shape, out object? _)).IsFalse()
				.Because("the Task shapes are served only over a service that needs asynchronous initialization or runtime arguments");
			await That(shop.GetResolvability(shape, null).Status).IsEqualTo(AwaitenResolvabilityStatus.NotRegistered);
			await That(() => shop.Resolve(shape)).Throws<InvalidOperationException>()
				.WithMessage("No registration for type*served only over*'*Plain' needs neither*resolve '*Plain' directly*ResolveAsync*").AsWildcard()
				.Because("the service is registered, so the miss names it as what to resolve");
			await That(() => shop.ResolveAsync(shape)).Throws<InvalidOperationException>()
				.WithMessage("*resolve '*Plain' directly*").AsWildcard();
		}

		[Test]
		[Arguments(typeof(Task<Task<Plain>>))]
		[Arguments(typeof(Func<Func<Task<Plain>>>))]
		[Arguments(typeof(Lazy<Task<Owned<Plain>>>))]
		public async Task ShapeNotServedOverAnyService_ReportsTheMissingRegistration(Type shape)
		{
			await using Shop.Root shop = new();

			await That(() => shop.Resolve(shape)).Throws<InvalidOperationException>()
				.WithMessage("No registration for type*on this container.").AsWildcard()
				.Because("an async-initialized service would not get this shape either, so needing no initialization is not why it is missing");
		}

		[Test]
		public async Task TaskOfAnUnregisteredService_StillReportsTheMissingRegistration()
		{
			await using Shop.Root shop = new();

			await That(() => shop.Resolve(typeof(Task<Unregistered>))).Throws<InvalidOperationException>()
				.WithMessage("No registration for type*on this container.").AsWildcard()
				.Because("there is no service to name");
		}
	}

	public sealed class RequestingType
	{
		[Test]
		public async Task AsyncRequestingTypeFactory_HandsOutTheProductWithoutARequestingType()
		{
			await using Shop.Root shop = new();

			ILabel label = await (Task<ILabel>)shop.Resolve(typeof(Task<ILabel>));
			Func<Task<ILabel>> factory = (Func<Task<ILabel>>)shop.Resolve(typeof(Func<Task<ILabel>>));
			Lazy<Task<ILabel>> lazy = (Lazy<Task<ILabel>>)shop.Resolve(typeof(Lazy<Task<ILabel>>));

			await That(label.RequestingType).IsNull()
				.Because("a top-level resolve has no requesting consumer");
			await That((await factory()).RequestingType).IsNull();
			await That((await lazy.Value).RequestingType).IsNull();
		}
	}

	public sealed class Parameterized
	{
		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task FuncOfTask_BuildsAnInitializedInstanceFromTheArguments(bool fromScope)
		{
			await using Shop.Root shop = new();
			await using Shop.Scope scope = shop.CreateScope();
			IAwaitenScope resolver = fromScope ? scope : shop;

			Func<int, Task<Brew>> factory = (Func<int, Task<Brew>>)resolver.Resolve(typeof(Func<int, Task<Brew>>));
			Brew brew = await factory(3);

			await That(brew.Size).IsEqualTo(3)
				.Because("the factory forwards the runtime argument");
			await That(brew.Initialized).IsTrue();
		}

		[Test]
		[Arguments(false)]
		[Arguments(true)]
		public async Task FuncOfTaskOfOwned_BuildsAnInitializedInstanceInAHandle(bool fromScope)
		{
			await using Shop.Root shop = new();
			await using Shop.Scope scope = shop.CreateScope();
			IAwaitenScope resolver = fromScope ? scope : shop;

			Func<int, Task<Owned<Brew>>> factory = (Func<int, Task<Owned<Brew>>>)resolver.Resolve(typeof(Func<int, Task<Owned<Brew>>>));
			await using Owned<Brew> owned = await factory(5);

			await That(owned.Value.Size).IsEqualTo(5);
			await That(owned.Value.Initialized).IsTrue();
		}

		[Test]
		public async Task FuncOfTaskOverASynchronousService_BuildsFromTheArgumentsInAScope()
		{
			await using Shop.Root shop = new();
			Mug mug;
			await using (Shop.Scope scope = shop.CreateScope())
			{
				Func<int, Task<Mug>> factory = (Func<int, Task<Mug>>)scope.Resolve(typeof(Func<int, Task<Mug>>));
				mug = await factory(7);

				await That(mug.Size).IsEqualTo(7);
			}

			await That(mug.Disposed).IsTrue()
				.Because("the scope the factory was resolved from tracks what it builds");
		}

		[Test]
		public async Task FuncOfTaskOfOwnedOverASynchronousService_HandsTheInstanceToTheCaller()
		{
			await using Shop.Root shop = new();

			Func<int, Task<Owned<Mug>>> factory = (Func<int, Task<Owned<Mug>>>)shop.Resolve(typeof(Func<int, Task<Owned<Mug>>>));
			Owned<Mug> owned = await factory(2);
			await owned.DisposeAsync();

			await That(owned.Value.Size).IsEqualTo(2);
			await That(owned.Value.Disposed).IsTrue()
				.Because("the handle disposes what it owns, so the root may hand out the factory");
		}

		[Test]
		public async Task ResolveAsyncOfFuncOfTask_HandsOutTheFactory()
		{
			await using Shop.Root shop = new();

			Func<int, Task<Brew>> factory = (Func<int, Task<Brew>>)await shop.ResolveAsync(typeof(Func<int, Task<Brew>>));

			await That((await factory(1)).Initialized).IsTrue();
		}

		[Test]
		public async Task ResolveOfTheSynchronousFunc_ThrowsGuidanceTowardsTheTaskShape()
		{
			await using Shop.Root shop = new();

			await That(() => shop.Resolve(typeof(Func<int, Brew>))).Throws<InvalidOperationException>()
				.WithMessage("*asynchronous initialization*Func<int, System.Threading.Tasks.Task<*Brew>>*").AsWildcard()
				.Because("the synchronous factory cannot await the initializer, so the guidance names the factory that can");
		}
	}

	public sealed class StrictLifetimeSafety
	{
		[Test]
		[Arguments(typeof(Task<Kettle>))]
		[Arguments(typeof(Func<Task<Kettle>>))]
		[Arguments(typeof(Func<int, Task<Mug>>))]
		public async Task ShapeOverADisposableTransient_IsWithheldOnTheRootButServedByAScope(Type shape)
		{
			await using Shop.Root shop = new();
			await using Shop.Scope scope = shop.CreateScope();

			await That(() => shop.Resolve(shape)).Throws<InvalidOperationException>()
				.WithMessage("*withheld*").AsWildcard()
				.Because("every resolution from the root would track a disposable there for the container's lifetime");
			await That(shop.TryResolve(shape, out object? _)).IsFalse();
			await That(shop.GetResolvability(shape, null).Status).IsEqualTo(AwaitenResolvabilityStatus.WithheldOnRoot);
			await That(scope.Resolve(shape)).IsNotNull()
				.Because("a child scope bounds what it builds");
		}

		[Test]
		[Arguments(typeof(Task<Owned<Kettle>>))]
		[Arguments(typeof(Func<Task<Owned<Kettle>>>))]
		[Arguments(typeof(Lazy<Task<Kettle>>))]
		[Arguments(typeof(Func<int, Task<Owned<Mug>>>))]
		public async Task LeakFreeShapeOverADisposableTransient_IsServedByTheRoot(Type shape)
		{
			await using Shop.Root shop = new();

			await That(shop.GetResolvability(shape, null).Status).IsEqualTo(AwaitenResolvabilityStatus.Resolvable);
			await That(shop.Resolve(shape)).IsNotNull()
				.Because("a handle disposes what it builds, and a Lazy builds at most once");
		}

		[Test]
		public async Task FuncOfTaskOfOwned_DisposesTheTransientWithTheHandle()
		{
			await using Shop.Root shop = new();

			Func<Task<Owned<Kettle>>> factory = (Func<Task<Owned<Kettle>>>)shop.Resolve(typeof(Func<Task<Owned<Kettle>>>));
			Owned<Kettle> owned = await factory();
			await owned.DisposeAsync();

			await That(owned.Value.Initialized).IsTrue();
			await That(owned.Value.Disposed).IsTrue();
		}

		[Test]
		public async Task LooseLifetimeSafety_ServesTheFactoryOnTheRoot()
		{
			await using LooseShop.Root shop = new();

			Func<Task<Kettle>> factory = (Func<Task<Kettle>>)shop.Resolve(typeof(Func<Task<Kettle>>));

			await That((await factory()).Initialized).IsTrue();
		}
	}

	public sealed class Resolvability
	{
		[Test]
		[Arguments(typeof(Task<IStore>))]
		[Arguments(typeof(Func<Task<IStore>>))]
		[Arguments(typeof(Lazy<Task<IStore>>))]
		[Arguments(typeof(Task<Owned<IStore>>))]
		[Arguments(typeof(Func<Task<Owned<IStore>>>))]
		[Arguments(typeof(Func<int, Task<Brew>>))]
		[Arguments(typeof(Func<int, Task<Owned<Brew>>>))]
		public async Task TaskShape_IsResolvable(Type shape)
		{
			await using Shop.Root shop = new();

			AwaitenResolvability answer = shop.GetResolvability(shape, null);

			await That(answer.Status).IsEqualTo(AwaitenResolvabilityStatus.Resolvable)
				.Because("the task shapes are handed out synchronously, whatever they await");
		}

		[Test]
		[Arguments(typeof(Func<IStore>))]
		[Arguments(typeof(Owned<IStore>))]
		[Arguments(typeof(Func<int, Brew>))]
		public async Task SynchronousShapeOverAnAsyncService_IsAsyncOnlyWithTheReasonResolveThrows(Type shape)
		{
			await using Shop.Root shop = new();

			AwaitenResolvability answer = shop.GetResolvability(shape, null);

			await That(answer.Status).IsEqualTo(AwaitenResolvabilityStatus.AsyncOnly);
			await That(() => shop.Resolve(shape)).Throws<InvalidOperationException>()
				.WithMessage(answer.Reason!)
				.Because("the probe reports the reason Resolve throws");
		}

		[Test]
		public async Task LazyOfTaskOfOwned_IsNotServed()
		{
			await using Shop.Root shop = new();

			await That(shop.TryResolve(typeof(Lazy<Task<Owned<IStore>>>), out object? _)).IsFalse()
				.Because("an Owned handle cannot be produced through Lazy, which injection rejects as AWT121");
			await That(shop.GetResolvability(typeof(Lazy<Task<Owned<IStore>>>), null).Status).IsEqualTo(AwaitenResolvabilityStatus.NotRegistered);
		}
	}

	public sealed class Unregistered;

	[Container]
	[AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
	[Singleton<Store, IStore>]
	[Singleton<Plain>]
	[Transient<Brew>]
	[Transient<Mug>]
	[Transient<Kettle>]
	[Transient<ILabel>(Factory = nameof(CreateLabelAsync))]
	public static partial class Shop
	{
		private static async Task<Label> CreateLabelAsync([RequestingType] Type? requestingType)
		{
			await Task.Yield();
			return new Label(requestingType);
		}
	}

	[Container]
	[Singleton<IReadOnlyList<Plain>>(Factory = nameof(CreatePlainsAsync))]
	public static partial class Pantry
	{
		private static async Task<IReadOnlyList<Plain>> CreatePlainsAsync()
		{
			await Task.Yield();
			return [new Plain()];
		}
	}

	[Container(LifetimeSafety = LifetimeSafety.Loose)]
	[AsyncInitializer<IInitializable>(nameof(IInitializable.InitializeAsync))]
	[Transient<Kettle>]
	public static partial class LooseShop;
}
