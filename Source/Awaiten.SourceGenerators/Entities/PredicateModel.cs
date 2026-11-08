using Awaiten.SourceGenerators.Internals;

namespace Awaiten.SourceGenerators.Entities;

/// <summary>
///     The <c>When</c> predicate of a conditional decorator chain link: the fully qualified static <see cref="Member" />
///     the root reads, whether it is a method that is <see cref="Invoked" />, and the method's <see cref="Parameters" />,
///     classified like a lifecycle hook's and restricted to services the root shares. A predicate without parameters is
///     read once in the <c>Root</c> constructor; one with parameters is evaluated once, on first use, after its services.
///     <see cref="DeclaredTypes" /> are the parameters' types as the predicate declares them, which a diagnostic names
///     where a variance redirect rewrote the service type.
/// </summary>
internal sealed record PredicateModel(string Member, bool Invoked, EquatableArray<ParameterModel> Parameters, EquatableArray<string> DeclaredTypes = default)
{
	/// <summary>Whether the predicate takes services, so the root evaluates it lazily instead of in its constructor.</summary>
	public bool IsLazy => Parameters.Count > 0;
}
