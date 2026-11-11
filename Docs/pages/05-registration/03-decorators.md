---
description: Wrap a service in decorators that share its interface, for closed, keyed and open generic registrations.
---

# Decorators

A decorator wraps a service in another that shares its interface. It is how you add logging, retries, or caching without touching the real implementation. The consumer asks for the interface and gets the wrapped version. It cannot tell the difference, and it cannot bypass the wrapper.

## Wrap a service

Register the real service, then add `[Decorate<TDecorator, TService>]`. The decorator takes the inner service as a constructor parameter.

```csharp
public sealed class LoggingTerminal(IPaymentTerminal inner) : IPaymentTerminal
{
    public Task ChargeAsync(decimal amount) { /* log, then */ return inner.ChargeAsync(amount); }
}

[Container]
[Singleton<CardTerminal, IPaymentTerminal>]
// highlight-next-line
[Decorate<LoggingTerminal, IPaymentTerminal>]
public static partial class CoffeeShop;

// Resolve<IPaymentTerminal>() returns LoggingTerminal(CardTerminal)
```

## Chain several

Stack decorators by declaring more than one. They chain in declaration order, and the last declared ends up outermost.

```csharp
[Singleton<CardTerminal, IPaymentTerminal>]
[Decorate<RetryingTerminal, IPaymentTerminal>]
[Decorate<LoggingTerminal, IPaymentTerminal>]
// result: LoggingTerminal(RetryingTerminal(CardTerminal))
```

```mermaid
flowchart LR
    consumer["Consumer of<br/>IPaymentTerminal"] --> logging["LoggingTerminal<br/>declared last, outermost"] --> retrying["RetryingTerminal<br/>declared first"] --> card["CardTerminal<br/>the registration"]
```

Need a different order? Set `Order`. A higher `Order` sits further out, and decorators with the same `Order` keep their declaration order. Here the `LoggingTerminal` stays outermost although it is declared first:

```csharp
[Decorate<LoggingTerminal, IPaymentTerminal>(Order = 1)]
[Decorate<RetryingTerminal, IPaymentTerminal>]
```

## Collections are decorated too

If a service has several implementations, each one is wrapped. A consumer that asks for `IEnumerable<IPaymentTerminal>` gets a collection of decorated terminals, so neither a plain resolve nor the collection reaches an undecorated instance. A [`WhenInjectedInto`](./05-keyed-services.md#bind-a-variant-to-a-consumer-with-wheninjectedinto) registration is wrapped by the same chain.

## Keyed registrations

By default a decorator wraps the unkeyed registrations of its service only. A registration with a `Key` stays undecorated, so a keyed resolve, a `[FromKey]` parameter and a [keyed dictionary](../06-resolution/04-keyed-dictionaries.md) get the bare implementation:

```csharp
[Singleton<CardTerminal, IPaymentTerminal>]
[Singleton<CashTerminal, IPaymentTerminal>(Key = "backup")]
[Decorate<LoggingTerminal, IPaymentTerminal>]

// Resolve<IPaymentTerminal>()         returns LoggingTerminal(CardTerminal)
// Resolve<IPaymentTerminal>("backup") returns CashTerminal
```

This holds even when the same implementation is registered both with and without a key. A service registered only under keys has nothing to decorate ([AWT123](../09-diagnostics.md#awt123)).

To decorate keyed registrations, name the keys in `Keys`, where `null` stands for the unkeyed registrations, or set `AllKeys` to decorate every registration of the service:

```csharp
[Singleton<CardTerminal, IPaymentTerminal>]
[Singleton<CashTerminal, IPaymentTerminal>(Key = "backup")]
[Singleton<SpareTerminal, IPaymentTerminal>(Key = "spare")]
[Decorate<LoggingTerminal, IPaymentTerminal>(Keys = new object?[] { null, "backup" })]
[Decorate<RetryingTerminal, IPaymentTerminal>(AllKeys = true)]

// Resolve<IPaymentTerminal>()         returns RetryingTerminal(LoggingTerminal(CardTerminal))
// Resolve<IPaymentTerminal>("backup") returns RetryingTerminal(LoggingTerminal(CashTerminal))
// Resolve<IPaymentTerminal>("spare")  returns RetryingTerminal(SpareTerminal)
```

Each key gets its own chain of the decorators that select it, ordered by `Order` and declaration order as above. A decorated key is decorated everywhere: a keyed resolve, a `[FromKey]` parameter and the keyed dictionary all get the chain. The open generic `typeof` form below takes `Keys` and `AllKeys` the same way and decorates the closings registered under the selected keys.

:::note
A key in `Keys` that matches no registration is a build error ([AWT210](../09-diagnostics.md#awt210)), and so are an empty `Keys`, which selects nothing ([AWT123](../09-diagnostics.md#awt123)), and setting `Keys` together with `AllKeys` ([AWT211](../09-diagnostics.md#awt211)).
:::

## Decorate every closing of an open generic

A pipeline behavior wraps *every* closing of an open generic service (`IHandler<PlaceOrder>`, `IHandler<Refund>`, and the rest) with one attribute. The generic `[Decorate<,>]` form only takes closed types, so use the `typeof` form with unbound generics instead:

```csharp
public sealed class LoggingBehavior<T>(IHandler<T> inner) : IHandler<T>
{
    public Task HandleAsync(T request) { /* log, then */ return inner.HandleAsync(request); }
}

public sealed class Checkout(IHandler<PlaceOrder> handler);

[Container]
[Transient(typeof(Handler<>), typeof(IHandler<>))]
// highlight-next-line
[Decorate(typeof(LoggingBehavior<>), typeof(IHandler<>))]
[Transient<Checkout>]   // needs IHandler<PlaceOrder>, so that closing is in the graph
public static partial class CoffeeShop;

// Resolve<IHandler<PlaceOrder>>() returns LoggingBehavior<PlaceOrder>(Handler<PlaceOrder>)
```

Awaiten synthesizes a closed decorator for every closing already in the graph, however it was registered: an open generic, an explicit closed registration, or a scan. Each closing then behaves exactly like a hand-written closed decorator: it joins its collection, respects `Order`, and interleaves with any explicit `[Decorate<D, IHandler<PlaceOrder>>]` on that one closing by the same rules.

An open decorator with no closing to wrap decorates nothing. A module or a base container meant for [extension](./09-extending-containers.md) can declare it for the closings its consumers register; in any other container it warns ([AWT249](../09-diagnostics.md#awt249)), which a base meant only for extension suppresses at the attribute.

The decorator's arity must match the service's, and it must expose the service with its type parameters in declaration order (`LoggingBehavior<T> : IHandler<T>`). A closing whose type arguments violate the decorator's constraints is skipped with a diagnostic. Like the closed form, a decorator's *own* generic dependencies are supplied only when some other registration already expands them into the graph.

## Apply a decorator conditionally

Some decorators belong only in some runs of the application: an audit log behind a command-line switch, or extra tracing in one deployment. Name a predicate in `When`, and the root decides once whether the decorator is applied. The predicate is a static `bool` method, property or field of the type that declares the `[Decorate]`:

```csharp
public static class ShopSwitches
{
    public static bool AuditPayments { get; set; }
}

public sealed class AuditedTerminal(IPaymentTerminal inner) : IPaymentTerminal;

[Container]
[Singleton<CardTerminal, IPaymentTerminal>]
// highlight-next-line
[Decorate<AuditedTerminal, IPaymentTerminal>(When = nameof(AuditPayments))]
public static partial class CoffeeShop
{
    private static bool AuditPayments => ShopSwitches.AuditPayments;
}
```

Set the switch at the entry point, before you create the root:

```csharp
ShopSwitches.AuditPayments = Environment.GetCommandLineArgs().Contains("--audit");
await using CoffeeShop.Root shop = new();

// Resolve<IPaymentTerminal>() returns AuditedTerminal(CardTerminal) with --audit, else CardTerminal
```

A predicate without parameters is evaluated once per root, when the root is created and before any [eager](../04-core-concepts/01-lifetimes.md#build-a-singleton-at-startup) singleton is built. Changing the switch afterwards does not affect that root, and a new root reads it again. Decorators that name the same predicate share one evaluation. The `typeof` form takes `When` too, and the predicate then decides for every closing.

When the predicate is `false`, the decorator is never built: it is not initialized, not tracked for disposal, and its own dependencies are never resolved. The other decorators of the chain keep their order, and a collection, a keyed dictionary or a composite gets the same chain as a plain resolve. A [context-aware factory](../06-resolution/07-context-aware-factories.md) behind a decorator that is off receives a `null` requesting type, as if it were resolved directly.

The graph is still validated with the decorator in place, whatever the predicate returns. Its dependencies must be registered, a captive dependency or a cycle through it is a build error, and [lifetime safety](../04-core-concepts/04-lifetime-safety.md) treats a disposable decorator as built. An async-initialized decorator keeps the service async even in a run where it is off, so resolve the service with `ResolveAsync` either way.

<details>
<summary>Build it at startup only when it is on</summary>

`Eager = true` on the decorated singleton builds it when the root is created, through the decorator when that is on and without it otherwise. To build it at startup only in the runs that apply the decorator, resolve it at the entry point instead:

```csharp
await using CoffeeShop.Root shop = new();
if (ShopSwitches.AuditPayments)
{
    await shop.ResolveAsync<IPaymentTerminal>(cancellationToken);
}
```

</details>

<details>
<summary>Let the predicate take services</summary>

A predicate can take services, for example settings the host binds from its configuration:

```csharp
public sealed class AuditSettings
{
    public bool Enabled { get; init; }
}

[Container]
[ImportService<AuditSettings>]
[Singleton<CardTerminal, IPaymentTerminal>]
[Decorate<AuditedTerminal, IPaymentTerminal>(When = nameof(AuditEnabled))]
public static partial class CoffeeShop
{
    private static bool AuditEnabled(AuditSettings settings) => settings.Enabled;
}
```

Such a predicate is still evaluated once per root, but the first time a decorator it guards is needed, once its services are resolved; an eager singleton evaluates it while it is built. Concurrent first resolves wait for that one evaluation, and a predicate that throws is evaluated again on the next use.

Because it decides for the whole root, it takes only services the root shares as one instance it builds synchronously: singletons, `Instance` registrations and [imported services](../07-hosting/02-external-services.mdx) that are not initialized asynchronously.

</details>

### Rules for predicates

- `When` names a static `bool` method, property or field of the type that declares the `[Decorate]`. Anything else, including a method returning `Task<bool>`, is a build error ([AWT246](../09-diagnostics.md#awt246)).
- A predicate on a [module](./08-modules/index.md) or an [extended container](./09-extending-containers.md) must be internal or public ([AWT247](../09-diagnostics.md#awt247)). How a container in another assembly calls an internal one is described in [non-public members](./08-modules/02-library-module-rules.md#non-public-members) for a module and in [switch an inherited decorator off](./09-extending-containers.md#switch-an-inherited-decorator-off) for a base container.
- A predicate parameter the root cannot supply once, such as a scoped service, a `Func<T>` or an async-initialized singleton, is a build error ([AWT248](../09-diagnostics.md#awt248)), and so is one that needs the decorated service ([AWT102](../09-diagnostics.md#awt102)).
- A service that reaches the decorated service only through a `Func<T>` or `Lazy<T>` is allowed. If it calls that `Func<T>` or `Lazy<T>` in its constructor, the predicate is needed while it is being evaluated, and the resolve throws an `InvalidOperationException` that names the predicate.

## Lifetime and disposal

A decorator inherits the lifetime of what it decorates. Wrap a singleton and the decorator is a singleton. A decorator of an [imported service](#decorate-an-imported-service) is transient. On teardown the chain is disposed outermost first, so the decorator is disposed before its inner service.

## Async inner services

If the inner service is async-initialized, the whole chain is async-tainted. Resolve it with `ResolveAsync`. A synchronous `Resolve` throws at run time, and on a variable of the generated `Root` or `Scope` type it gets a build warning ([AWT219](../09-diagnostics.md#awt219)). A synchronous `Func<T>`, `Lazy<T>` or `Owned<T>` over the chain is a build error ([AWT119](../09-diagnostics.md#awt119)), and so is one over a service that reaches the chain through its dependencies ([AWT120](../09-diagnostics.md#awt120)). See [Async initialization](../04-core-concepts/03-async-initialization.md).

## Decorate an imported service

A decorator also wraps a service the host provides through [`[ImportService<T>]`](../07-hosting/02-external-services.mdx). The container owns the decorator, and the host keeps its instance.

```csharp
[Container]
[ImportService<IPaymentTerminal>]
[Decorate<LoggingTerminal, IPaymentTerminal>]
public static partial class CoffeeShop;

// Resolve<IPaymentTerminal>() returns LoggingTerminal(the host's terminal)
```

An imported service has no lifetime the decorator could inherit, so the chain is transient: each resolve builds the decorators again, and the innermost asks the host for its instance, as an undecorated import does.

The container tracks and disposes the decorators like any transient and never disposes the host's instance. A singleton that depends on the service holds one chain, as it would hold the bare import.

- **Keys.** The host decides the keys it provides a service under, so name them in `Keys`: `Keys = ["backup"]` wraps the instance the host provides under `"backup"`. `AllKeys` and an empty `Keys` are a build error ([AWT123](../09-diagnostics.md#awt123)).
- **Generics.** An open decorator wraps each closed `[ImportService<T>]` of its service, and a closed decorator can wrap a closing of an open import. An open decorator over an open import is a build error ([AWT123](../09-diagnostics.md#awt123)), since the container learns its closings only from the dependencies that ask for them.
- **`[ImportServices]`.** It declares no service imported, so a decorated service needs its own `[ImportService<T>]`.

## Rules

- The decorated service must be registered or imported ([AWT123](../09-diagnostics.md#awt123)).
- The decorator needs a constructor parameter that accepts the decorated service, which receives the inner instance ([AWT124](../09-diagnostics.md#awt124)). Its type may be one the service converts to, such as a base interface. A `[FromKey]` parameter never receives it.
- When several parameters accept the service, the one whose type converts to the types of all the others receives the inner instance. Without exactly one such parameter the decorator is ambiguous ([AWT124](../09-diagnostics.md#awt124)).
- The open generic `typeof` form requires an unbound generic ([AWT127](../09-diagnostics.md#awt127)) of matching arity ([AWT125](../09-diagnostics.md#awt125)), and skips a closing whose type arguments violate the decorator's constraints with a warning ([AWT171](../09-diagnostics.md#awt171)).

:::tip[When not to reach for this]
A conditional decorator answers one question: should this cross-cutting wrapper exist in this run? Reach for it when the answer is fixed at startup, by a command-line switch, an environment or a deployment profile, and the decorator should then cost nothing.

- **Not for decisions while the application runs.** The predicate is evaluated once. When the decision can change at run time, or differs per request or tenant, it is domain logic: let the decorator take its options (or a feature-flag service) and pass calls through when it is disabled.
- **Not for ambient state.** Keep the switch a value the entry point sets once, or the predicate turns into an [ambient context](../03-design-principles.md#ambient-context).
- **Not for several applications.** When several switches start to combine, or one swaps the core implementation rather than wrapping it, you have two applications: give each its own container, share their common wiring in a [module](./08-modules/index.md), and pick the root at the entry point.
- **Not for test-only switches.** When a decorator is only off in tests, a test container that [extends the production one](./09-extending-containers.md#switch-an-inherited-decorator-off) is the better seam.

See [Design principles](../03-design-principles.md#when-power-becomes-a-smell).
:::

## Where to go next

- [Composites](./04-composites.md) to expose many implementations as one.
- [Collections](../06-resolution/03-collections.md) for resolving every implementation.
