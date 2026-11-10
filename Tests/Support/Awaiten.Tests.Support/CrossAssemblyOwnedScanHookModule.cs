namespace Awaiten.Tests.Support;

/// <summary>The marker of the appliances <see cref="OwnedScanHookModule" /> scans for.</summary>
public interface IOwnedHookAppliance;

/// <summary>
///     A public implementation <see cref="OwnedScanHookModule" /> both registers and matches with its own scan, both
///     naming the same hook, which counts how often it ran on the instance.
/// </summary>
public sealed class OwnedHookBoiler : IOwnedHookAppliance
{
	/// <summary>How often the module's <c>OnActivated</c> hook ran on the instance.</summary>
	public int Activations { get; internal set; }
}

/// <summary>
///     A <c>[Module]</c> whose registration owns the instance its scan matches, with the scan naming the registration's
///     <c>internal</c> hook, so a container of another assembly runs the hook once through the generated wrappers.
/// </summary>
[Module]
[Scan<IOwnedHookAppliance>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton, OnActivated = nameof(Activate))]
[Singleton<OwnedHookBoiler>(OnActivated = nameof(Activate))]
public static partial class OwnedScanHookModule
{
	internal static void Activate(IOwnedHookAppliance appliance) => ((OwnedHookBoiler)appliance).Activations++;
}
