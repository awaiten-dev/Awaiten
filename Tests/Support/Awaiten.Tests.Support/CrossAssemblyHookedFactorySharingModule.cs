using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Awaiten.Tests.Support;

/// <summary>A greeter another container registers with hooks of its own beside the module, which shares the module's factory.</summary>
public interface IHookedFactorySharingGreeter;

/// <summary>
///     A public implementation whose <c>[Inject]</c> property another assembly cannot set, started by the module's
///     convention, which records every step that ran on it.
/// </summary>
public sealed class HookedFactorySharingGreeter : IHookedFactorySharingGreeter, ISelfCompiledStartable, IDisposable
{
	private FactorySharingClock _clock = null!;

	[Inject]
	internal FactorySharingClock Clock
	{
		get => _clock;
		set
		{
			_clock = value;
			ClockAssignments++;
		}
	}

	/// <summary>How often the property was set, so a test sees that the instance is built once.</summary>
	public int ClockAssignments { get; private set; }

	/// <summary>The steps that ran on the greeter, in order.</summary>
	public List<string> Steps { get; } = new();

	/// <inheritdoc />
	public Task StartAsync(CancellationToken cancellationToken)
	{
		Steps.Add("convention");
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public void Dispose() => Steps.Add("dispose");
}

/// <summary>
///     A <c>[Module]</c> that builds a greeter with an internal <c>[Inject]</c> property through its generated factory and
///     starts it by its convention, which a container in another assembly calls for its own hooked registration too.
/// </summary>
[Module]
[AsyncInitializer<ISelfCompiledStartable>(nameof(ISelfCompiledStartable.StartAsync))]
[Singleton<FactorySharingClock>]
[Singleton<HookedFactorySharingGreeter, IHookedFactorySharingGreeter>]
public static partial class CrossAssemblyHookedFactorySharingModule;
