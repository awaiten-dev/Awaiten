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
public interface IMultiScopedRoaster : IMultiCounters;

/// <summary>The marker of the transient match of <see cref="MultiExposureScanModule" />.</summary>
public interface IMultiTransientPlugin;

/// <summary>The matching interface of the transient match of <see cref="MultiExposureScanModule" />.</summary>
public interface IMultiTransientRoaster : IMultiCounters;

/// <summary>What the scoped and transient matches of <see cref="MultiExposureScanModule" /> count.</summary>
public interface IMultiCounters
{
	/// <summary>How often the module's <c>OnActivated</c> hook ran on the instance.</summary>
	int Activations { get; }

	/// <summary>How often the module's <c>OnRelease</c> hook ran on the instance.</summary>
	int Releases { get; }

	/// <summary>How often a container disposed the instance.</summary>
	int Disposals { get; }
}

/// <summary>The marker of the async-only disposable match of <see cref="MultiExposureScanModule" />.</summary>
public interface IMultiAsyncPlugin;

/// <summary>The matching interface of the async-only disposable match of <see cref="MultiExposureScanModule" />.</summary>
public interface IMultiAsyncRoaster
{
	/// <summary>How often a container disposed the instance asynchronously.</summary>
	int Disposals { get; }
}

/// <summary>The marker of the matches of <see cref="MixedExposureScanModule" />.</summary>
public interface IMixedPlugin;

/// <summary>The matching interface of the only match of <see cref="MixedExposureScanModule" /> exposed under several interfaces.</summary>
public interface IMixedBeta;

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

internal abstract class CountedRoaster : IMultiCounters, IDisposable
{
	public int Activations { get; set; }

	public int Releases { get; set; }

	public int Disposals { get; private set; }

	public void Dispose() => Disposals++;
}

internal sealed class MultiScopedRoaster : CountedRoaster, IMultiScopedPlugin, IMultiScopedRoaster;

internal sealed class MultiTransientRoaster : CountedRoaster, IMultiTransientPlugin, IMultiTransientRoaster;

internal sealed class MultiAsyncRoaster : IMultiAsyncPlugin, IMultiAsyncRoaster, IAsyncDisposable
{
	public int Disposals { get; private set; }

	public ValueTask DisposeAsync()
	{
		Disposals++;
		return default;
	}
}

internal sealed class MixedAlpha : IMixedPlugin;

internal sealed class MixedBeta : IMixedPlugin, IMixedBeta;

internal sealed class MixedGamma : IMixedPlugin;

/// <summary>The marker of the match of <see cref="PublicMultiExposureScanModule" />.</summary>
public interface IPublicMultiPlugin;

/// <summary>The matching interface of the match of <see cref="PublicMultiExposureScanModule" />.</summary>
public interface IPublicMultiRoaster
{
	/// <summary>How often the module's <c>OnActivated</c> hook ran on the instance.</summary>
	int Activations { get; }

	/// <summary>How often a container disposed the instance.</summary>
	int Disposals { get; }
}

/// <summary>A public match of <see cref="PublicMultiExposureScanModule" />, which a consumer may register itself.</summary>
public sealed class PublicMultiRoaster : IPublicMultiPlugin, IPublicMultiRoaster, IDisposable
{
	/// <inheritdoc />
	public int Activations { get; internal set; }

	/// <inheritdoc />
	public int Disposals { get; private set; }

	/// <inheritdoc />
	public void Dispose() => Disposals++;
}

/// <summary>
///     A <c>[Module]</c> whose scan exposes its public match under its marker and its matching interface, which the module
///     shares through a generated instance handle.
/// </summary>
[Module]
[Scan<IPublicMultiPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton, OnActivated = nameof(Activate))]
public static partial class PublicMultiExposureScanModule
{
	internal static void Activate(PublicMultiRoaster roaster) => roaster.Activations++;
}

/// <summary>The marker of the match of <see cref="PlainPublicMultiExposureScanModule" />.</summary>
public interface IPlainPublicMultiPlugin;

/// <summary>The matching interface of the match of <see cref="PlainPublicMultiExposureScanModule" />.</summary>
public interface IPlainPublicMultiRoaster
{
	/// <summary>How often a container disposed the instance.</summary>
	int Disposals { get; }
}

/// <summary>A public match of <see cref="PlainPublicMultiExposureScanModule" />, which a consumer may register itself.</summary>
public sealed class PlainPublicMultiRoaster : IPlainPublicMultiPlugin, IPlainPublicMultiRoaster, IDisposable
{
	/// <inheritdoc />
	public int Disposals { get; private set; }

	/// <inheritdoc />
	public void Dispose() => Disposals++;
}

/// <summary>
///     A <c>[Module]</c> whose scan without hooks exposes its public match under its marker and its matching interface,
///     which the module shares through a generated instance handle, so a consumer's registration of the match can own the
///     instance.
/// </summary>
[Module]
[Scan<IPlainPublicMultiPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]
public static partial class PlainPublicMultiExposureScanModule;

/// <summary>
///     A <c>[Module]</c> whose scans expose each internal match under its marker and its matching interface, which the
///     module shares through a generated instance handle per match.
/// </summary>
[Module]
[AsyncInitializer<IMultiWarmable>(nameof(IMultiWarmable.WarmAsync))]
[Scan<IMultiPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton, OnActivated = nameof(Activate), OnRelease = nameof(Release))]
[Scan<IMultiScopedPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Scoped, OnActivated = nameof(ActivateScoped), OnRelease = nameof(ReleaseScoped))]
[Scan<IMultiTransientPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Transient, OnActivated = nameof(ActivateTransient), OnRelease = nameof(ReleaseTransient))]
[Scan<IMultiAsyncPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]
public static partial class MultiExposureScanModule
{
	internal static void Activate(MultiRoaster roaster) => roaster.Activations++;

	internal static void Release(MultiRoaster roaster) => roaster.Releases++;

	internal static void ActivateScoped(MultiScopedRoaster roaster) => roaster.Activations++;

	internal static void ReleaseScoped(MultiScopedRoaster roaster) => roaster.Releases++;

	internal static void ActivateTransient(MultiTransientRoaster roaster) => roaster.Activations++;

	internal static void ReleaseTransient(MultiTransientRoaster roaster) => roaster.Releases++;
}

/// <summary>The marker of the matches of <see cref="ArgumentMultiExposureScanModule" />.</summary>
public interface IArgumentPlugin
{
	/// <summary>The runtime argument the instance was built from.</summary>
	string Order { get; }
}

/// <summary>The matching interface of the match of <see cref="ArgumentMultiExposureScanModule" /> built from a runtime argument.</summary>
public interface IArgumentRoaster : IArgumentPlugin;

/// <summary>The matching interface of the async-initialized match of <see cref="ArgumentMultiExposureScanModule" />.</summary>
public interface IAsyncArgumentRoaster : IArgumentPlugin
{
	/// <summary>Whether the module's async initializer ran on the instance.</summary>
	bool Warmed { get; }
}

/// <summary>The convention <see cref="ArgumentMultiExposureScanModule" /> runs on its async-initialized match.</summary>
public interface IArgumentWarmable
{
	/// <summary>Warms the instance.</summary>
	Task WarmAsync();
}

internal sealed class ArgumentRoaster([Arg] string order) : IArgumentRoaster
{
	public string Order => order;
}

internal sealed class AsyncArgumentRoaster([Arg] string order) : IAsyncArgumentRoaster, IArgumentWarmable
{
	public string Order => order;

	public bool Warmed { get; private set; }

	public Task WarmAsync()
	{
		Warmed = true;
		return Task.CompletedTask;
	}
}

/// <summary>
///     A <c>[Module]</c> whose scan exposes each internal match built from a runtime argument under its marker and its
///     matching interface, which the module serves through a factory of its generated instance handle.
/// </summary>
[Module]
[AsyncInitializer<IArgumentWarmable>(nameof(IArgumentWarmable.WarmAsync))]
[Scan<IArgumentPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface)]
public static partial class ArgumentMultiExposureScanModule;

/// <summary>
///     A <c>[Module]</c> whose scan exposes one match under its marker and its matching interface and the others under
///     their marker only, so the marker's collection mixes a match served from a handle with matches built directly.
/// </summary>
[Module]
[Scan<IMixedPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]
public static partial class MixedExposureScanModule;
