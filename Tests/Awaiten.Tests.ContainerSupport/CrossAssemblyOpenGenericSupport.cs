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

/// <summary>
///     A <c>[Module]</c> in an assembly that grants <c>InternalsVisibleTo</c>, whose open generic implementation and hook
///     are private to it, so even a friend runs the hook through the generic wrapper the module generates.
/// </summary>
[Module]
[Singleton<OpenHookLog>]
[Scoped(typeof(FriendHookedOpenGenericModule.Cache<>), typeof(IHookedOpenCache<>), OnActivated = nameof(Activate), OnRelease = nameof(Release))]
public static partial class FriendHookedOpenGenericModule
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

/// <summary>
///     A production container in a referenced assembly whose open generic registration and hook are private to it, so a
///     test container that extends it runs the hook through the generic wrapper the base generates.
/// </summary>
[Container]
[Singleton<OpenHookLog>]
[Scoped(typeof(HookedOpenBaseContainer.Cache<>), typeof(IHookedOpenCache<>), OnActivated = nameof(Activate), OnRelease = nameof(Release))]
public static partial class HookedOpenBaseContainer
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

/// <summary>A public open generic journal whose internal <c>[Inject]</c> property a friend container sets itself.</summary>
public sealed class FriendInjectedOpenJournal<T> : IInjectedOpenJournal<T>
{
	[Inject]
	internal IOpenClock Injected { get; set; } = null!;

	/// <inheritdoc />
	public IOpenClock Clock => Injected;

	/// <inheritdoc />
	public IOpenClock? Audit { get; set; }
}

/// <summary>
///     A <c>[Module]</c> in an assembly that grants <c>InternalsVisibleTo</c>, whose public open generic journal a friend
///     container constructs itself, internal <c>[Inject]</c> property included.
/// </summary>
[Module]
[Singleton(typeof(FriendInjectedOpenJournal<>), typeof(IInjectedOpenJournal<>))]
public static partial class FriendInjectedOpenJournalModule;

/// <summary>A store per entity type, built from an open generic implementation private to <see cref="SupportOpenBaseContainer" />.</summary>
public interface IBaseOpenStore<T>
	where T : class
{
	/// <summary>The store the decorator wraps, or <see langword="null" /> for the undecorated store.</summary>
	IBaseOpenStore<T>? Inner { get; }

	/// <summary>Whether the container disposed the store.</summary>
	bool Disposed { get; }

	/// <summary>The clock the base's <c>[InjectProperty]</c> entry fills.</summary>
	IOpenClock? Clock { get; }
}

/// <summary>The clock <see cref="SupportOpenBaseContainer" /> registers.</summary>
public sealed class BaseOpenClock : IOpenClock;

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
///     so a test container in another assembly closes the generic factories the base generates, which also fill the
///     property of the base's <c>[InjectProperty]</c> entry.
/// </summary>
[Container]
[Singleton<BaseOpenClock, IOpenClock>]
[Scoped(typeof(SupportOpenBaseContainer.Store<>), typeof(IBaseOpenStore<>))]
[InjectProperty(typeof(SupportOpenBaseContainer.Store<>), nameof(IBaseOpenStore<BaseOpenOrder>.Clock))]
[Decorate(typeof(SupportOpenBaseContainer.Guard<>), typeof(IBaseOpenStore<>))]
[Scoped<BaseOpenCheckout>]
public static partial class SupportOpenBaseContainer
{
	private sealed class Store<T> : IBaseOpenStore<T>, System.IDisposable
		where T : class
	{
		public IBaseOpenStore<T>? Inner => null;

		public bool Disposed { get; private set; }

		public IOpenClock? Clock { get; set; }

		public void Dispose() => Disposed = true;
	}

	private sealed class Guard<T>(IBaseOpenStore<T> inner) : IBaseOpenStore<T>
		where T : class
	{
		public IBaseOpenStore<T>? Inner => inner;

		public bool Disposed => inner.Disposed;

		public IOpenClock? Clock => inner.Clock;
	}
}
