namespace Awaiten.Tests.Support;

/// <summary>The clock each brew of <see cref="CrossAssemblyAliasedInjectionModule" /> takes through an internal <c>[Inject]</c> property.</summary>
public interface IAliasedInjectionClock;

/// <summary>The clock behind <see cref="IAliasedInjectionClock" />.</summary>
public sealed class AliasedInjectionClock : IAliasedInjectionClock;

internal interface IAliasedInjectionKettle;

internal sealed class AliasedInjectionKettle : IAliasedInjectionKettle;

/// <summary>A brew whose internal <c>[Inject]</c> property counts how often it is assigned.</summary>
public abstract class AliasedInjectionBrew
{
	private IAliasedInjectionClock? _clock;

	/// <summary>The clock the property received, or <see langword="null" />.</summary>
	public IAliasedInjectionClock? AssignedClock => _clock;

	/// <summary>How often the property was assigned.</summary>
	public int ClockAssignments { get; private set; }

	[Inject]
	internal IAliasedInjectionClock Clock
	{
		get => _clock!;
		set
		{
			_clock = value;
			ClockAssignments++;
		}
	}
}

/// <summary>The service of the singleton brew.</summary>
public interface IAliasedSingletonBrew;

/// <summary>A singleton brew.</summary>
public sealed class AliasedSingletonBrew : AliasedInjectionBrew, IAliasedSingletonBrew;

/// <summary>The service of the scoped brew.</summary>
public interface IAliasedScopedBrew;

/// <summary>A scoped brew.</summary>
public sealed class AliasedScopedBrew : AliasedInjectionBrew, IAliasedScopedBrew;

/// <summary>The service of the transient brew.</summary>
public interface IAliasedTransientBrew;

/// <summary>A transient brew.</summary>
public sealed class AliasedTransientBrew : AliasedInjectionBrew, IAliasedTransientBrew;

/// <summary>The service of the brew built from a runtime argument.</summary>
public interface IAliasedArgumentBrew;

/// <summary>A brew built from a runtime argument.</summary>
public sealed class AliasedArgumentBrew([Arg] string order) : AliasedInjectionBrew, IAliasedArgumentBrew
{
	/// <summary>The order the brew was built from.</summary>
	public string Order { get; } = order;
}

/// <summary>
///     A <c>[Module]</c> that declares an internal service, so it builds each brew into an instance handle, and the clock
///     into the internal property on the instance the handle holds.
/// </summary>
[Module]
[Singleton<AliasedInjectionKettle, IAliasedInjectionKettle>]
[Singleton<AliasedInjectionClock, IAliasedInjectionClock>]
[Singleton<AliasedSingletonBrew, IAliasedSingletonBrew>]
[Scoped<AliasedScopedBrew, IAliasedScopedBrew>]
[Transient<AliasedTransientBrew, IAliasedTransientBrew>]
[Transient<AliasedArgumentBrew, IAliasedArgumentBrew>]
public static partial class CrossAssemblyAliasedInjectionModule;

/// <summary>A <c>[Module]</c> without anything internal that registers each brew of <see cref="CrossAssemblyAliasedInjectionModule" /> under itself.</summary>
[Module]
[Singleton<AliasedSingletonBrew>]
[Scoped<AliasedScopedBrew>]
[Transient<AliasedTransientBrew>]
[Transient<AliasedArgumentBrew>]
public static partial class CrossAssemblyAliasedInjectionOtherModule;
