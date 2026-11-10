using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Awaiten.Tests.Support;

/// <summary>A device whose internal async initializer convention records each run.</summary>
public abstract class InternalConventionDevice
{
	/// <summary>The hooks and initializers that ran on this instance, in order.</summary>
	public List<string> Steps { get; } = new();

	/// <summary>The token the convention received, or <see langword="null" /> before it ran.</summary>
	public CancellationToken? Token { get; private set; }

	internal Task WarmUpAsync(CancellationToken cancellationToken)
	{
		Steps.Add("warmed");
		Token = cancellationToken;
		return Task.CompletedTask;
	}
}

/// <summary>A brewer the container constructs itself, initialized by an internal method and an internal interface.</summary>
public sealed class InternalConventionBrewer : InternalConventionDevice, IInternalConventionCalibrated
{
	Task IInternalConventionCalibrated.CalibrateAsync()
	{
		Steps.Add("calibrated");
		return Task.CompletedTask;
	}
}

internal interface IInternalConventionCalibrated
{
	Task CalibrateAsync();
}

/// <summary>A grinder the module registers under one service.</summary>
public interface IInternalConventionGrinder
{
	/// <summary>The device behind the service.</summary>
	InternalConventionDevice Device { get; }
}

internal sealed class InternalConventionGrinder : InternalConventionDevice, IInternalConventionGrinder
{
	public InternalConventionDevice Device => this;
}

/// <summary>A kettle the module registers under two services.</summary>
public interface IInternalConventionKettle
{
	/// <summary>The device behind the service.</summary>
	InternalConventionDevice Device { get; }
}

/// <summary>The second service of the kettle.</summary>
public interface IInternalConventionHeater
{
	/// <summary>The device behind the service.</summary>
	InternalConventionDevice Device { get; }
}

internal sealed class InternalConventionKettle : InternalConventionDevice, IInternalConventionKettle, IInternalConventionHeater
{
	public InternalConventionDevice Device => this;
}

/// <summary>The marker the module's scan matches.</summary>
public interface IInternalConventionPlugin
{
	/// <summary>The device behind the service.</summary>
	InternalConventionDevice Device { get; }
}

internal sealed class InternalConventionPlugin : InternalConventionDevice, IInternalConventionPlugin
{
	public InternalConventionDevice Device => this;
}

/// <summary>A mill the module produces through private factories that return the internal implementation.</summary>
public interface IInternalConventionMill
{
	/// <summary>The device behind the service.</summary>
	InternalConventionDevice Device { get; }
}

/// <summary>The mill the module produces through a private async factory.</summary>
public interface IInternalConventionAsyncMill
{
	/// <summary>The device behind the service.</summary>
	InternalConventionDevice Device { get; }
}

internal sealed class InternalConventionMill : InternalConventionDevice, IInternalConventionMill, IInternalConventionAsyncMill, IInternalConventionCalibrated
{
	public InternalConventionDevice Device => this;

	public Task CalibrateAsync()
	{
		Steps.Add("calibrated");
		return Task.CompletedTask;
	}
}

/// <summary>
///     A <c>[Module]</c> whose async initializer conventions are internal, on a public implementation the container
///     constructs, on internal implementations under one and two services, on a scan match and on internal implementations
///     private factories return under a public service, so an importing container in
///     another assembly without <c>InternalsVisibleTo</c> runs them through the wrappers and handles the module generates.
/// </summary>
[Module]
[AsyncInitializer<InternalConventionDevice>(nameof(InternalConventionDevice.WarmUpAsync))]
[AsyncInitializer<IInternalConventionCalibrated>(nameof(IInternalConventionCalibrated.CalibrateAsync))]
[Transient<InternalConventionBrewer>(OnActivated = nameof(Activate))]
[Scoped<InternalConventionGrinder, IInternalConventionGrinder>]
[Singleton<InternalConventionKettle, IInternalConventionKettle>]
[Singleton<InternalConventionKettle, IInternalConventionHeater>]
[Scan<IInternalConventionPlugin>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
[Transient<IInternalConventionMill>(Factory = nameof(MakeMill))]
[Singleton<IInternalConventionAsyncMill>(Factory = nameof(MakeMillAsync))]
public static partial class InternalConventionModule
{
	/// <summary>Records that the brewer was activated, which happens before its async initializers run.</summary>
	/// <param name="brewer">The activated brewer.</param>
	public static void Activate(InternalConventionBrewer brewer) => brewer.Steps.Add("activated");

	private static InternalConventionMill MakeMill() => new();

	private static Task<InternalConventionMill> MakeMillAsync() => Task.FromResult(new InternalConventionMill());
}
