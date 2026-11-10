namespace Awaiten.Tests.Support;

/// <summary>A kettle whose implementation stays internal, which a container extending an importer still builds.</summary>
public interface IFactorySharingKettle;

/// <summary>The clock the internal <c>[Inject]</c> property of <see cref="FactorySharingGreeter" /> takes.</summary>
public sealed class FactorySharingClock;

/// <summary>A greeter another container registers beside the module, which shares the module's factory.</summary>
public interface IFactorySharingGreeter;

internal sealed class FactorySharingKettle : IFactorySharingKettle;

/// <summary>A public implementation whose <c>[Inject]</c> property another assembly cannot set.</summary>
public sealed class FactorySharingGreeter : IFactorySharingGreeter
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

	/// <summary>The clock the module's generated factory set.</summary>
	public FactorySharingClock InjectedClock => Clock;

	/// <summary>How often the property was set, so a test sees that the instance is built once.</summary>
	public int ClockAssignments { get; private set; }
}

/// <summary>
///     A <c>[Module]</c> that builds an internal kettle and a greeter with an internal <c>[Inject]</c> property through
///     its generated factories, which a container in another assembly calls for each of its registrations of them.
/// </summary>
[Module]
[Singleton<FactorySharingKettle, IFactorySharingKettle>]
[Singleton<FactorySharingClock>]
[Singleton<FactorySharingGreeter, IFactorySharingGreeter>]
public static partial class CrossAssemblyFactorySharingModule;
