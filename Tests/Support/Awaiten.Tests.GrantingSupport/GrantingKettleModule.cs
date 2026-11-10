namespace Awaiten.Tests.GrantingSupport;

/// <summary>The service <see cref="GrantingKettleModule" /> registers <see cref="GrantingKettle" /> under.</summary>
public interface IGrantingKettle;

/// <summary>
///     A public kettle with an internal constructor, which a container in an assembly this one grants nothing builds
///     only through the factory a module generates for it.
/// </summary>
public sealed class GrantingKettle : IGrantingKettle
{
	internal GrantingKettle() { }

	/// <summary>How often an activation hook ran on this instance.</summary>
	public int Activations { get; internal set; }
}

/// <summary>A <c>[Module]</c> that builds <see cref="GrantingKettle" /> through its generated factory.</summary>
[Module]
[Transient<GrantingKettle, IGrantingKettle>]
public static partial class GrantingKettleModule;
