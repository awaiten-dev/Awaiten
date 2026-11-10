using Awaiten.Tests.GrantingSupport;

namespace Awaiten.Tests.Elsewhere;

/// <summary>
///     A <c>[Module]</c> of an assembly that the assembly of <see cref="GrantingKettleModule" /> grants
///     <c>InternalsVisibleTo</c>, which builds <see cref="GrantingKettle" /> through a generated factory of its own and
///     counts its activations. It is the only module of its assembly that registers the kettle, since several would
///     build it through a shared handle instead.
/// </summary>
[Module]
[Transient<GrantingKettle>(OnActivated = nameof(Activate))]
public static partial class ElsewhereKettleModule
{
	internal static void Activate(GrantingKettle kettle) => kettle.Activations++;
}
