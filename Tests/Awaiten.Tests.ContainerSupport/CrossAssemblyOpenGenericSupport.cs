using Awaiten.Tests.Support;

namespace Awaiten.Tests.ContainerSupport;

/// <summary>A store per entity type, built from an open generic implementation private to <see cref="FriendOpenGenericModule" />.</summary>
public interface IFriendOpenStore<T>
	where T : class
{
	/// <summary>The clock the container passed to the store.</summary>
	IOpenClock Clock { get; }

	/// <summary>Whether the container disposed the store.</summary>
	bool Disposed { get; }
}

/// <summary>
///     A <c>[Module]</c> in an assembly that grants <c>InternalsVisibleTo</c>, whose open generic implementation is private
///     to it, so even a friend closes the generic factory the module generates.
/// </summary>
[Module]
[Scoped(typeof(FriendOpenGenericModule.Store<>), typeof(IFriendOpenStore<>))]
public static partial class FriendOpenGenericModule
{
	private sealed class Store<T>(IOpenClock clock) : IFriendOpenStore<T>, System.IDisposable
		where T : class
	{
		public IOpenClock Clock => clock;

		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;
	}
}

/// <summary>A store per entity type, built from an open generic implementation private to <see cref="SupportOpenBaseContainer" />.</summary>
public interface IBaseOpenStore<T>
	where T : class
{
	/// <summary>The store the decorator wraps, or <see langword="null" /> for the undecorated store.</summary>
	IBaseOpenStore<T>? Inner { get; }

	/// <summary>Whether the container disposed the store.</summary>
	bool Disposed { get; }
}

/// <summary>An entity type the checkout of <see cref="SupportOpenBaseContainer" /> stores.</summary>
public sealed class BaseOpenOrder;

/// <summary>A checkout that takes a closing of the private open generic store of <see cref="SupportOpenBaseContainer" />.</summary>
public sealed class BaseOpenCheckout(IBaseOpenStore<BaseOpenOrder> orders)
{
	/// <summary>The store of orders.</summary>
	public IBaseOpenStore<BaseOpenOrder> Orders => orders;
}

/// <summary>
///     A production container in a referenced assembly whose open generic registration and decorator are private to it,
///     so a test container in another assembly closes the generic factories the base generates.
/// </summary>
[Container]
[Scoped(typeof(SupportOpenBaseContainer.Store<>), typeof(IBaseOpenStore<>))]
[Decorate(typeof(SupportOpenBaseContainer.Guard<>), typeof(IBaseOpenStore<>))]
[Scoped<BaseOpenCheckout>]
public static partial class SupportOpenBaseContainer
{
	private sealed class Store<T> : IBaseOpenStore<T>, System.IDisposable
		where T : class
	{
		public IBaseOpenStore<T>? Inner => null;

		public bool Disposed { get; private set; }

		public void Dispose() => Disposed = true;
	}

	private sealed class Guard<T>(IBaseOpenStore<T> inner) : IBaseOpenStore<T>
		where T : class
	{
		public IBaseOpenStore<T>? Inner => inner;

		public bool Disposed => inner.Disposed;
	}
}
