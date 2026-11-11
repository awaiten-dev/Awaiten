using System;

namespace Awaiten;

/// <summary>
///     Marks a parameter of a constructor or of a <c>Factory</c> method as supplied at resolve time rather than from the
///     object graph. A service with one or more <c>[Arg]</c> parameters is parameterized: it is built fresh on every call
///     and is resolvable only through a <c>Func&lt;TArg…, TService&gt;</c>, <c>Func&lt;TArg…, Owned&lt;TService&gt;&gt;</c>,
///     <c>Func&lt;TArg…, Task&lt;TService&gt;&gt;</c> or <c>Func&lt;TArg…, Task&lt;Owned&lt;TService&gt;&gt;&gt;</c> whose
///     leading type arguments are the runtime arguments, matched positionally to the marked parameters. The remaining
///     parameters resolve from the graph.
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
public sealed class ArgAttribute : Attribute;
