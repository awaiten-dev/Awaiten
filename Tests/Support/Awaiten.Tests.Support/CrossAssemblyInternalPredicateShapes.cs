#if CONTAINER_SUPPORT
using Awaiten.Tests.Support;

namespace Awaiten.Tests.ContainerSupport;
#else
namespace Awaiten.Tests.Support;

/// <summary>The switches the internal predicates of <see cref="InternalPredicateModule" /> read, and how often they ran.</summary>
public static class InternalPredicateSwitch
{
	/// <summary>Whether the tracing decorator applies to the next root.</summary>
	public static bool Traced { get; set; }

	/// <summary>Whether the auditing decorator applies to the next root.</summary>
	public static bool Audited { get; set; }

	/// <summary>How often the property predicate was read.</summary>
	public static int TracedEvaluations { get; set; }

	/// <summary>How often the method predicate was called.</summary>
	public static int AuditedEvaluations { get; set; }

	/// <summary>Sets both switches and clears the counts.</summary>
	public static void Reset(bool traced, bool audited)
	{
		Traced = traced;
		Audited = audited;
		TracedEvaluations = 0;
		AuditedEvaluations = 0;
	}
}

/// <summary>A gateway a module decorates as its internal predicates decide.</summary>
public interface IPredicateGateway
{
	/// <summary>The chain from this link inwards, for example <c>audited(traced(gateway))</c>.</summary>
	string Describe();
}

/// <summary>Whether a base container's decorator applies, which an extending container replaces.</summary>
public sealed class PredicateMonitoring
{
	/// <summary>Whether the monitoring decorator applies.</summary>
	public bool Enabled { get; init; } = true;
}

/// <summary>A terminal a base container decorates as its internal predicate decides.</summary>
public interface IPredicateTerminal
{
	/// <summary>The chain from this link inwards, for example <c>monitored(terminal)</c>.</summary>
	string Describe();
}

/// <summary>The terminal of <see cref="InternalPredicateBaseContainer" />.</summary>
public sealed class PredicateTerminal : IPredicateTerminal
{
	/// <inheritdoc />
	public string Describe() => "terminal";
}

/// <summary>The decorator of <see cref="InternalPredicateBaseContainer" />.</summary>
public sealed class MonitoredPredicateTerminal(IPredicateTerminal inner) : IPredicateTerminal
{
	/// <inheritdoc />
	public string Describe() => "monitored(" + inner.Describe() + ")";
}

/// <summary>
///     A <c>[Container]</c> whose decorator an internal predicate guards, which a container that extends it in another
///     assembly without <c>InternalsVisibleTo</c> calls through the public wrapper the base generates.
/// </summary>
[Container]
[Singleton<PredicateMonitoring>]
[Singleton<PredicateTerminal, IPredicateTerminal>]
[Decorate<MonitoredPredicateTerminal, IPredicateTerminal>(When = nameof(IsMonitored))]
public static partial class InternalPredicateBaseContainer
{
	internal static bool IsMonitored(PredicateMonitoring monitoring) => monitoring.Enabled;
}
#endif

internal sealed class PredicateAudit
{
#pragma warning disable CA1822 // a predicate reads it from the injected service
	public bool Enabled => InternalPredicateSwitch.Audited;
#pragma warning restore CA1822
}

internal sealed class PredicateGateway : IPredicateGateway
{
	public string Describe() => "gateway";
}

internal sealed class TracedPredicateGateway(IPredicateGateway inner) : IPredicateGateway
{
	public string Describe() => "traced(" + inner.Describe() + ")";
}

internal sealed class AuditedPredicateGateway(IPredicateGateway inner) : IPredicateGateway
{
	public string Describe() => "audited(" + inner.Describe() + ")";
}

/// <summary>
///     A <c>[Module]</c> whose decorators internal predicates guard: a property, and a method that takes an internal
///     service, which a container in another assembly without <c>InternalsVisibleTo</c> calls through the public
///     wrappers the module generates, and a friend calls directly.
/// </summary>
[Module]
[Singleton<PredicateGateway, IPredicateGateway>]
[Singleton<PredicateAudit>]
[Decorate<TracedPredicateGateway, IPredicateGateway>(When = nameof(Traced))]
[Decorate<AuditedPredicateGateway, IPredicateGateway>(When = nameof(Audited))]
#if CONTAINER_SUPPORT
public static partial class FriendInternalPredicateModule
#else
public static partial class InternalPredicateModule
#endif
{
	internal static bool Traced
	{
		get
		{
			InternalPredicateSwitch.TracedEvaluations++;
			return InternalPredicateSwitch.Traced;
		}
	}

	internal static bool Audited(PredicateAudit audit)
	{
		InternalPredicateSwitch.AuditedEvaluations++;
		return audit.Enabled;
	}
}
