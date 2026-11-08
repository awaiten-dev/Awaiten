namespace Awaiten.Tests.PolyfillFreeSupport;

/// <summary>A service the container in a consumer without the Nullable package builds.</summary>
public interface IPolyfillFreeClock;

/// <summary>The implementation of <see cref="IPolyfillFreeClock" />.</summary>
public sealed class PolyfillFreeClock : IPolyfillFreeClock;

/// <summary>A container in a consumer whose framework has no public <c>NotNullWhenAttribute</c>.</summary>
[Container]
[Singleton<PolyfillFreeClock, IPolyfillFreeClock>]
[Singleton<PolyfillFreeClock, IPolyfillFreeClock>(Key = "keyed")]
public static partial class PolyfillFreeContainer;

/// <summary>Reads <see cref="PolyfillFreeContainer" /> through <c>TryResolve</c> with nullable warnings as errors.</summary>
public static class PolyfillFreeLookup
{
	/// <summary>The type name of the clock, which <c>TryResolve</c> hands out as not null when it returns true.</summary>
	public static string ClockName(PolyfillFreeContainer.Root root)
		=> root.TryResolve(typeof(IPolyfillFreeClock), out object? clock) ? clock.GetType().Name : string.Empty;
}
