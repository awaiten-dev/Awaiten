using Awaiten.SourceGenerators.Internals;

namespace Awaiten.SourceGenerators.Entities;

/// <summary>
///     A single constructor parameter: the underlying service type it resolves (the <c>T</c> inside a
///     <c>Func&lt;T&gt;</c> / <c>Lazy&lt;T&gt;</c>, or the parameter type itself for a direct dependency)
///     and how it is delivered. <c>FuncArgTypes</c> holds the leading runtime-argument types of a
///     <c>Func&lt;TArg…, T&gt;</c> relationship (empty otherwise). <c>Key</c> is the resolution key
///     requested by a <c>[FromKey]</c> on the parameter (null for the unkeyed registration), so the
///     dependency is wired to the matching keyed registration. <c>ProducesOwned</c> marks a
///     <c>Func&lt;…, Owned&lt;T&gt;&gt;</c> relationship whose produced value is wrapped in an
///     <c>Owned&lt;T&gt;</c> disposal handle (a bare <c>Owned&lt;T&gt;</c> uses <see cref="DependencyKind.Owned" />
///     instead). <c>Location</c> is the parameter's own source location, so a diagnostic about how it
///     consumes its dependency can point at the parameter rather than the registration. Relationship-typed,
///     runtime-argument and owned parameters launder async taint and never capture their target, so they
///     contribute no edges for taint or captive-dependency analysis. The deferred forms (Func/Lazy and their
///     async variants) also break cycles; the bare eager <c>Owned&lt;T&gt;</c> / <c>Task&lt;T&gt;</c> resolve
///     at construction time and so still close cycles (the construction graph in <c>BuildConstructionGraph</c>).
///     <c>AwaitedCollectionType</c> is the inner collection shape of an
///     <see cref="DependencyKind.AwaitedEnumerable" /> (e.g. <c>IReadOnlyList&lt;T&gt;</c> for a
///     <c>Task&lt;IReadOnlyList&lt;T&gt;&gt;</c> parameter), which the materialized array must be typed as so
///     the produced task's result type matches the parameter exactly; for an
///     <see cref="DependencyKind.AwaitedKeyedCollection" /> it is the inner dictionary shape
///     (<c>IReadOnlyDictionary&lt;string, T&gt;</c>), used to type the produced task. For both it also recognizes a
///     registration of the inner shape, whose <c>Task</c> the parameter then becomes (null for every other kind).
///     <c>KeyType</c> is the requested dictionary key type of a keyed collection (the <c>string</c> keyword or a
///     fully-qualified enum type), used to synthesize a <c>Dictionary&lt;TKey, T&gt;</c> of the right key type,
///     including the empty case where no keyed registration exists to derive it from (null for every other kind).
///     <c>DispatchMembers</c> are the entries of a <see cref="DependencyKind.TypeDispatch" /> dictionary, whose
///     <c>ServiceType</c> is the base interface <c>TBase</c> (empty for every other kind).
///     <c>NestedNullable</c> marks a declared type with a nullable annotation below its top level
///     (<c>IRepo&lt;string?&gt;</c>, <c>Task&lt;IStore?&gt;</c>, <c>IStore?[]</c>): the generated code spells every type
///     without annotations, like the registration it finds, so it forgives the value's nullability, which a
///     nullable-enabled consumer would otherwise get as CS8619 or CS8620. For a <c>Func&lt;TArg…, T&gt;</c> only the
///     produced <c>T</c> counts, since the generated factory spells its argument types as declared.
///     <c>DeclaredType</c> is an <see cref="DependencyKind.Arg" /> parameter's type with its nullable annotations, and
///     <c>DeclaredFuncArgTypes</c> are the <c>FuncArgTypes</c> with the consumer's: the runtime arguments keep them
///     from the consumer's <c>Func</c> to the <c>[Arg]</c> parameter, so the compiler checks a nullable argument
///     against the parameter, while <c>ServiceType</c> and <c>FuncArgTypes</c> match them without annotations. An
///     <c>[Arg]</c> type built from a type parameter has no <c>DeclaredType</c>: its resolver spells it as registered,
///     and the factory forgives the argument it forwards.
///     <c>ImportedInner</c> marks the <see cref="DependencyKind.External" /> inner parameter of the innermost decorator
///     over an imported service, which a conditional link returns when its predicate is false.
///     <c>ConsumerOwned</c> marks a <c>Func</c> or dispatch dictionary over a build-on-demand service that its consumer
///     lists in <c>ConsumerOwned</c>: the container hands each instance it builds over untracked.
/// </summary>
internal sealed record ParameterModel(
	string ServiceType,
	DependencyKind Kind,
	EquatableArray<string> FuncArgTypes = default,
	string? Key = null,
	LocationInfo? Location = null,
	bool ProducesOwned = false,
	string? AwaitedCollectionType = null,
	string? KeyType = null,
	EquatableArray<DispatchMember> DispatchMembers = default,
	bool NestedNullable = false,
	string? DeclaredType = null,
	EquatableArray<string> DeclaredFuncArgTypes = default,
	bool ImportedInner = false,
	bool ClosingHandle = false,
	bool ConsumerOwned = false);
