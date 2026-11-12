using System;

namespace Awaiten.Tests.Support;

/// <summary>A disposable transient that a consumer of <see cref="CrossAssemblyConsumerOwnedModule" /> owns.</summary>
public sealed class OwnedBrewSession : IDisposable
{
	/// <summary>Whether the session was disposed.</summary>
	public bool Disposed { get; private set; }

	/// <inheritdoc />
	public void Dispose() => Disposed = true;
}

/// <summary>Starts brew sessions it owns.</summary>
public interface IOwningBarista
{
	/// <summary>Starts a session, which the caller disposes.</summary>
	OwnedBrewSession Start();
}

internal sealed class OwningBarista(Func<OwnedBrewSession> sessions) : IOwningBarista
{
	public OwnedBrewSession Start() => sessions();
}

/// <summary>A <c>[Module]</c> whose internal singleton owns what its <c>Func</c> builds.</summary>
[Module]
[Transient<OwnedBrewSession>]
[Singleton<OwningBarista, IOwningBarista>(ConsumerOwned = [typeof(OwnedBrewSession),])]
public static partial class CrossAssemblyConsumerOwnedModule;
