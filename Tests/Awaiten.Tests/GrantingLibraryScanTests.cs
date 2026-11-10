using Awaiten.Tests.GrantingSupport;

namespace Awaiten.Tests;

/// <summary>
///     The scan of a module whose assembly grants <c>InternalsVisibleTo</c> to another assembly takes the internal
///     services of its modules through their handles in a container of an assembly it grants nothing, as without the grant.
/// </summary>
public partial class GrantingLibraryScanTests
{
	[Test]
	public async Task AScanMatchAndItsHook_TakeTheInternalServiceThroughItsHandle()
	{
		await using GrantingScanContainer.Root container = new();

		IGrantingPlugin plugin = container.Resolve<IGrantingPlugin>();

		await That(plugin.WiredStore).IsSameAs(plugin.Store)
			.Because("the scan's factory and its activation hook both take the one singleton store through its handle");
		await That(container.Resolve<IGrantingPlugin>().Store).IsSameAs(plugin.Store);
	}

	[Container]
	[Import(typeof(GrantingPluginModule))]
	[Import(typeof(GrantingStoreModule))]
	public static partial class GrantingScanContainer;
}
