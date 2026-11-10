using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Awaiten.Tests.Elsewhere")]

namespace Awaiten.Tests.GrantingSupport;

/// <summary>The marker <see cref="GrantingPluginModule" /> scans for.</summary>
public interface IGrantingPlugin
{
	/// <summary>The internal store the plugin's constructor received.</summary>
	object Store { get; }

	/// <summary>The internal store the scan's activation hook received.</summary>
	object? WiredStore { get; }
}

internal sealed class GrantingStore;

internal sealed class GrantingRoaster(GrantingStore store) : IGrantingPlugin
{
	public object Store => store;

	public object? WiredStore { get; set; }
}

/// <summary>
///     A <c>[Module]</c> whose scan match and activation hook take the internal store that another module of its
///     assembly registers, in an assembly that grants <c>InternalsVisibleTo</c> to an assembly other than the consumer.
/// </summary>
[Module]
[Scan<IGrantingPlugin>(As = ScanAs.Marker, OnActivated = nameof(Wire))]
public static partial class GrantingPluginModule
{
	internal static void Wire(GrantingRoaster roaster, GrantingStore store) => roaster.WiredStore = store;
}

/// <summary>A <c>[Module]</c> that registers the internal store <see cref="GrantingPluginModule" /> takes.</summary>
[Module]
[Singleton<GrantingStore>]
public static partial class GrantingStoreModule;
