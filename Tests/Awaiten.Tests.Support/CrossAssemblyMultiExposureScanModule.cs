using System;
using System.Threading.Tasks;

namespace Awaiten.Tests.Support;

/// <summary>The marker of the singleton match of <see cref="MultiExposureScanModule" />.</summary>
public interface IMultiPlugin;

/// <summary>The matching interface of the singleton match of <see cref="MultiExposureScanModule" />.</summary>
public interface IMultiRoaster
{
	/// <summary>How often the module's <c>OnActivated</c> hook ran on the instance.</summary>
	int Activations { get; }

	/// <summary>How often the module's <c>OnRelease</c> hook ran on the instance.</summary>
	int Releases { get; }

	/// <summary>How often the module's async initializer ran on the instance.</summary>
	int Warmings { get; }

	/// <summary>How often a container disposed the instance.</summary>
	int Disposals { get; }
}

/// <summary>The convention <see cref="MultiExposureScanModule" /> runs on its singleton match.</summary>
public interface IMultiWarmable
{
	/// <summary>Warms the instance.</summary>
	Task WarmAsync();
}

/// <summary>The marker of the scoped match of <see cref="MultiExposureScanModule" />.</summary>
public interface IMultiScopedPlugin;

/// <summary>The matching interface of the scoped match of <see cref="MultiExposureScanModule" />.</summary>
public interface IMultiScopedRoaster
{
	/// <summary>How often a container disposed the instance.</summary>
	int Disposals { get; }
}

/// <summary>The marker of the transient match of <see cref="MultiExposureScanModule" />.</summary>
public interface IMultiTransientPlugin;

/// <summary>The matching interface of the transient match of <see cref="MultiExposureScanModule" />.</summary>
public interface IMultiTransientRoaster;

internal sealed class MultiRoaster : IMultiPlugin, IMultiRoaster, IMultiWarmable, IDisposable
{
	public int Activations { get; set; }

	public int Releases { get; set; }

	public int Warmings { get; private set; }

	public int Disposals { get; private set; }

	public Task WarmAsync()
	{
		Warmings++;
		return Task.CompletedTask;
	}

	public void Dispose() => Disposals++;
}

internal sealed class MultiScopedRoaster : IMultiScopedPlugin, IMultiScopedRoaster, IDisposable
{
	public int Disposals { get; private set; }

	public void Dispose() => Disposals++;
}

internal sealed class MultiTransientRoaster : IMultiTransientPlugin, IMultiTransientRoaster;

/// <summary>
///     A <c>[Module]</c> whose scans expose each internal match under its marker and its matching interface, which the
///     module shares through a generated instance handle per match.
/// </summary>
[Module]
[AsyncInitializer<IMultiWarmable>(nameof(IMultiWarmable.WarmAsync))]
[Scan<IMultiPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton, OnActivated = nameof(Activate), OnRelease = nameof(Release))]
[Scan<IMultiScopedPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Scoped)]
[Scan<IMultiTransientPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Transient)]
public static partial class MultiExposureScanModule
{
	internal static void Activate(MultiRoaster roaster) => roaster.Activations++;

	internal static void Release(MultiRoaster roaster) => roaster.Releases++;
}
