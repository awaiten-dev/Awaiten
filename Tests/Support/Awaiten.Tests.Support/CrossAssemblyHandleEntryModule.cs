using System.Threading;
using System.Threading.Tasks;

namespace Awaiten.Tests.Support;

/// <summary>A clock a container of another assembly injects into <see cref="HandledBrewer" /> through an [InjectProperty] entry.</summary>
public interface IBrewerClock;

/// <summary>
///     A public brewer that <see cref="CrossAssemblyHandleEntryModule" /> builds through a generated handle, since it
///     registers it under an internal service, and that records whether its clock was set when the module's hook and
///     async initializer ran.
/// </summary>
public sealed class HandledBrewer : IHandledBrewer, IStartableBrewer
{
	/// <summary>The clock a container-side [InjectProperty] entry fills.</summary>
	public IBrewerClock? Clock { get; set; }

	/// <summary>Whether <see cref="Clock" /> was set when the module's OnActivated hook ran.</summary>
	public bool ClockSetWhenActivated { get; private set; }

	/// <summary>Whether <see cref="Clock" /> was set when the module's async initializer ran.</summary>
	public bool ClockSetWhenStarted { get; private set; }

	internal void Activate() => ClockSetWhenActivated = Clock is not null;

	Task IStartableBrewer.StartAsync(CancellationToken cancellationToken)
	{
		ClockSetWhenStarted = Clock is not null;
		return Task.CompletedTask;
	}
}

/// <summary>A user of the brewer the handle of <see cref="CrossAssemblyHandleEntryModule" /> holds.</summary>
public interface IHandledBrewerUser
{
	/// <summary>The brewer the user received.</summary>
	HandledBrewer Brewer { get; }
}

internal interface IHandledBrewer;

internal sealed class HandledBrewerUser(IHandledBrewer brewer) : IHandledBrewerUser
{
	public HandledBrewer Brewer => (HandledBrewer)brewer;
}

internal interface IStartableBrewer
{
	Task StartAsync(CancellationToken cancellationToken);
}

/// <summary>A <c>[Module]</c> that registers the public brewer under an internal service, with a hook and an async initializer.</summary>
[Module]
[AsyncInitializer<IStartableBrewer>(nameof(IStartableBrewer.StartAsync))]
[Singleton<HandledBrewer, IHandledBrewer>(OnActivated = nameof(Activate))]
[Singleton<HandledBrewerUser, IHandledBrewerUser>]
public static partial class CrossAssemblyHandleEntryModule
{
	internal static void Activate(HandledBrewer brewer) => brewer.Activate();
}
