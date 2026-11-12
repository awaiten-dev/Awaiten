using System.Collections.Generic;

namespace Awaiten.Tests.Support;

/// <summary>The switch the internal predicate of <see cref="CrossAssemblyEagerWhenModule" /> reads, and what it observed.</summary>
public static class EagerWhenModuleSwitch
{
	/// <summary>Whether the next root builds the client at startup and decorates it.</summary>
	public static bool Monitoring { get; set; }

	/// <summary>How often the predicate was read.</summary>
	public static int Evaluations { get; set; }

	/// <summary>The clients and decorators built, in order.</summary>
	public static List<string> Built { get; } = new();

	/// <summary>Sets the switch and clears what was observed.</summary>
	public static void Reset(bool monitoring)
	{
		Monitoring = monitoring;
		Evaluations = 0;
		Built.Clear();
	}
}

/// <summary>A client that starts polling once it exists.</summary>
public interface IEagerWhenClient
{
	/// <summary>The chain from this link inwards, for example <c>monitored(client)</c>.</summary>
	string Describe();
}

internal sealed class EagerWhenClient : IEagerWhenClient
{
	public EagerWhenClient() => EagerWhenModuleSwitch.Built.Add("client");

	public string Describe() => "client";
}

internal sealed class MonitoredEagerWhenClient : IEagerWhenClient
{
	private readonly IEagerWhenClient _inner;

	public MonitoredEagerWhenClient(IEagerWhenClient inner)
	{
		_inner = inner;
		EagerWhenModuleSwitch.Built.Add("monitored");
	}

	public string Describe() => "monitored(" + _inner.Describe() + ")";
}

/// <summary>
///     A <c>[Module]</c> whose client is built at startup and decorated only when an internal predicate allows it, which a
///     container in another assembly without <c>InternalsVisibleTo</c> calls through the public wrapper the module generates.
/// </summary>
[Module]
[Singleton<IEagerWhenClient>(Factory = nameof(CreateClient), EagerWhen = nameof(IsMonitoringEnabled))]
[Decorate<MonitoredEagerWhenClient, IEagerWhenClient>(When = nameof(IsMonitoringEnabled))]
public static partial class CrossAssemblyEagerWhenModule
{
	internal static bool IsMonitoringEnabled
	{
		get
		{
			EagerWhenModuleSwitch.Evaluations++;
			return EagerWhenModuleSwitch.Monitoring;
		}
	}

	internal static IEagerWhenClient CreateClient() => new EagerWhenClient();
}
