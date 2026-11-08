using Awaiten.Tests.PolyfillFreeSupport;

namespace Awaiten.Tests;

/// <summary>
///     A container generated in a .NET Framework or netstandard2.0 assembly without the Nullable package, where
///     <c>NotNullWhenAttribute</c> is not accessible, still compiles and resolves through <c>TryResolve</c>.
/// </summary>
public sealed class PolyfillFreeConsumerTests
{
	[Test]
	public async Task TryResolve_ReturnsTheRegisteredService()
	{
		using PolyfillFreeContainer.Root root = new();

		bool resolved = root.TryResolve(typeof(IPolyfillFreeClock), out object? clock);

		await That(resolved).IsTrue();
		await That(clock).Is<PolyfillFreeClock>()
			.Because("the generated TryResolve works without the nullable attribute on its out parameter");
	}

	[Test]
	public async Task KeyedTryResolve_ReturnsTheRegisteredService()
	{
		using PolyfillFreeContainer.Root root = new();

		bool resolved = root.TryResolve(typeof(IPolyfillFreeClock), "keyed", out object? clock);

		await That(resolved).IsTrue();
		await That(clock).Is<PolyfillFreeClock>()
			.Because("the generated keyed TryResolve works without the nullable attribute on its out parameter");
	}
}
