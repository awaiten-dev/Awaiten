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
