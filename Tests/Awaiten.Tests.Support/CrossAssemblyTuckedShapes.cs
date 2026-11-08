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
#endif

internal sealed class TuckedKettle : ITuckedKettle, IDisposable
{
	public bool Disposed { get; private set; }

	public void Dispose() => Disposed = true;
}

internal sealed class TuckedShelf : ITuckedShelf
{
	public bool Disposed => false;
}

/// <summary>
///     A <c>[Module]</c> whose private members return internal implementations of public services, which every
///     container calls through a wrapper the module generates: one that returns the implementation for a container that
///     names it, one that returns the service for any other.
/// </summary>
[Module]
[Singleton<TuckedKettle, ITuckedKettle>(Factory = nameof(MakeKettle))]
[Singleton<TuckedShelf, ITuckedShelf>(Instance = nameof(Shelf))]
#if CONTAINER_SUPPORT
public static partial class FriendTuckedShapeModule
#else
public static partial class TuckedShapeModule
#endif
{
	private static TuckedShelf Shelf { get; } = new();

	/// <summary>The shelf the module provides, for a test to compare against.</summary>
	public static ITuckedShelf ExpectedShelf => Shelf;

	private static TuckedKettle MakeKettle() => new();
}
#if CONTAINER_SUPPORT

/// <summary>A container in the module's own assembly that imports <see cref="FriendTuckedShapeModule" />.</summary>
[Container]
[Import(typeof(FriendTuckedShapeModule))]
public static partial class FriendTuckedShapeLocalContainer;
#endif
