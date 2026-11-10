using System;
#if CONTAINER_SUPPORT
using Awaiten.Tests.Support;

namespace Awaiten.Tests.ContainerSupport;
#else

namespace Awaiten.Tests.Support;

/// <summary>A public service a module produces through a private member that returns an internal implementation.</summary>
public interface ITuckedPart
{
	/// <summary>Whether the container disposed the part.</summary>
	bool Disposed { get; }
}

/// <summary>A kettle a module produces through a private factory.</summary>
public interface ITuckedKettle : ITuckedPart;

/// <summary>A shelf a module provides through a private instance.</summary>
public interface ITuckedShelf : ITuckedPart;

/// <summary>A pot a module produces through a private factory, under this and a second service.</summary>
public interface ITuckedPot : ITuckedPart;

/// <summary>The second service of the pot.</summary>
public interface ITuckedPan : ITuckedPart;

/// <summary>A rack a module provides through a private instance, under this and a second service.</summary>
public interface ITuckedRack : ITuckedPart;

/// <summary>The second service of the rack.</summary>
public interface ITuckedHook : ITuckedPart;

/// <summary>A spoon a module produces through an internal factory, under this and a second service.</summary>
public interface ITuckedSpoon : ITuckedPart;

/// <summary>The second service of the spoon.</summary>
public interface ITuckedLadle : ITuckedPart;

/// <summary>A scale a module produces through a private factory that takes an internal service.</summary>
public interface ITuckedScale
{
	/// <summary>Whether the factory received the internal service.</summary>
	bool Weighed { get; }
}
#endif

internal interface ITuckedWeight;

internal sealed class TuckedWeight : ITuckedWeight;

internal sealed class TuckedScale(bool weighed) : ITuckedScale
{
	public bool Weighed { get; } = weighed;
}

internal sealed class TuckedKettle : ITuckedKettle, IDisposable
{
	public bool Disposed { get; private set; }

	public void Dispose() => Disposed = true;
}

internal sealed class TuckedShelf : ITuckedShelf
{
	public bool Disposed => false;
}

internal class TuckedPart : IDisposable
{
	public bool Disposed { get; private set; }

	public void Dispose() => Disposed = true;
}

internal sealed class TuckedPot : TuckedPart, ITuckedPot, ITuckedPan;

internal sealed class TuckedRack : TuckedPart, ITuckedRack, ITuckedHook;

internal sealed class TuckedSpoon : TuckedPart, ITuckedSpoon, ITuckedLadle;

/// <summary>
///     A <c>[Module]</c> whose members that are not public produce internal implementations of public services: a private
///     factory and instance of one service each, which every container calls through a wrapper the module generates (one
///     that returns the implementation for a container that names it, one that returns the service for any other), and
///     members of two services or with an internal parameter, which a container in another assembly reaches through an
///     instance handle.
/// </summary>
[Module]
[Singleton<TuckedKettle, ITuckedKettle>(Factory = nameof(MakeKettle))]
[Singleton<TuckedShelf, ITuckedShelf>(Instance = nameof(Shelf))]
[Singleton<TuckedPot, ITuckedPot>(Factory = nameof(MakePot))]
[Singleton<TuckedPot, ITuckedPan>(Factory = nameof(MakePot))]
[Singleton<TuckedRack, ITuckedRack>(Instance = nameof(Rack))]
[Singleton<TuckedRack, ITuckedHook>(Instance = nameof(Rack))]
[Scoped<TuckedSpoon, ITuckedSpoon>(Factory = nameof(MakeSpoon))]
[Scoped<TuckedSpoon, ITuckedLadle>(Factory = nameof(MakeSpoon))]
[Singleton<TuckedWeight, ITuckedWeight>]
[Transient<ITuckedScale>(Factory = nameof(MakeScale))]
#if CONTAINER_SUPPORT
public static partial class FriendTuckedShapeModule
#else
public static partial class TuckedShapeModule
#endif
{
	private static TuckedShelf Shelf { get; } = new();

	private static TuckedRack Rack { get; } = new();

	/// <summary>The shelf the module provides, for a test to compare against.</summary>
	public static ITuckedShelf ExpectedShelf => Shelf;

	private static TuckedKettle MakeKettle() => new();

	private static TuckedPot MakePot() => new();

	internal static TuckedSpoon MakeSpoon() => new();

#pragma warning disable CA1859 // the factory returns the registered service, which is the shape under test
	private static ITuckedScale MakeScale(ITuckedWeight weight) => new TuckedScale(weight is TuckedWeight);
#pragma warning restore CA1859
}
#if CONTAINER_SUPPORT

/// <summary>A container in the module's own assembly that imports <see cref="FriendTuckedShapeModule" />.</summary>
[Container]
[Import(typeof(FriendTuckedShapeModule))]
public static partial class FriendTuckedShapeLocalContainer;
#endif
