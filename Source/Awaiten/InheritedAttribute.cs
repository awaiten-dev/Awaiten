using System;

namespace Awaiten;

/// <summary>
///     Marks a parameter of a <c>Factory</c> member of a container with <c>Extends</c> as the registration of the base
///     that the member's own registration replaces, for the same service and key. The replaced registration keeps its
///     lifetime, lifecycle hooks, async initialization and disposal, but serves nothing else: no resolve, collection or
///     decorator reaches it. Take the service as <c>T</c>, <c>Lazy&lt;T&gt;</c>, <c>Func&lt;T&gt;</c> or their async
///     forms; a <c>Func&lt;T&gt;</c> or <c>Lazy&lt;T&gt;</c> builds the replaced registration only when called.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class InheritedAttribute : Attribute;
