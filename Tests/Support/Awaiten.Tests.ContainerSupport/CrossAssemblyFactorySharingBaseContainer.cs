using Awaiten.Tests.Support;

namespace Awaiten.Tests.ContainerSupport;

/// <summary>
///     A container in a third assembly that another assembly extends, inheriting the registrations of
///     <see cref="CrossAssemblyFactorySharingModule" />, which neither assembly may construct itself.
/// </summary>
[Container]
[Import(typeof(CrossAssemblyFactorySharingModule))]
public static partial class CrossAssemblyFactorySharingBaseContainer;

/// <summary>A greeter a friend module registers, whose <c>[Inject]</c> property a friend container sets itself.</summary>
public sealed class FriendFactorySharingGreeter : IFactorySharingGreeter
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

	/// <summary>The clock the container set.</summary>
	public FactorySharingClock InjectedClock => Clock;

	/// <summary>How often the property was set, so a test sees that the instance is built once.</summary>
	public int ClockAssignments { get; private set; }
}

/// <summary>A <c>[Module]</c> of an assembly that grants the test assembly <c>InternalsVisibleTo</c>, with the shape of <see cref="CrossAssemblyFactorySharingModule" />.</summary>
[Module]
[Singleton<FactorySharingClock>]
[Singleton<FriendFactorySharingGreeter, IFactorySharingGreeter>]
public static partial class CrossAssemblyFriendFactorySharingModule;
