namespace Awaiten;

/// <summary>
///     Whether a <c>[Singleton]</c>/<c>[Transient]</c>/<c>[Scoped]</c> registration is an overridable default,
///     and how a collision between two such defaults is handled. A registration without <c>Fallback</c> of the
///     same service replaces an overridable default entirely, even in collections, except a scan match and, for a
///     closed default, the closing of an open generic registration, which both stay members behind the default.
///     Defaults that nothing else replaces are all collection members, and the first of them in precedence order
///     wins single resolution. Every default that stays a member is built and validated like any registration, also
///     when it does not win single resolution: a missing or captive dependency is a compile-time error, an eager one
///     is built at startup, and its hooks and disposal apply. The two overridable modes differ only in whether an
///     unresolved collision between them is reported. That choice is deliberately part of the registration:
///     it lets a module author decide per registration whether a competing default deserves a warning
///     (AWT148), which a consumer's project-wide <c>.editorconfig</c> severity cannot express.
/// </summary>
/// <remarks>
///     The generator reads these as their underlying int off the attribute's <c>TypedConstant</c> (it cannot
///     reference this type across the compilation boundary), so the numeric values are load-bearing: do not
///     renumber or reorder. See <c>NamedFallback</c> in the generator.
/// </remarks>
public enum Fallback
{
	/// <summary>A normal registration that participates fully; a conflict with another is reported (the default).</summary>
	None = 0,

	/// <summary>
	///     An overridable default that expects to be the only one: if another <c>Warn</c> registration provides
	///     the same service with nothing stronger to resolve the tie, AWT148 warns and the first declared wins.
	/// </summary>
	Warn = 1,

	/// <summary>
	///     An overridable default that defers silently: it is replaced like a <c>Warn</c> default, and nothing is
	///     reported if another default also provides the service.
	/// </summary>
	Silent = 2,
}
