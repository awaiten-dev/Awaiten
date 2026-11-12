---
description: Understand how strict lifetime safety keeps disposable transients from leaking on the root, and when to opt into loose mode.
---

# Lifetime safety

Disposable transients are easy to leak. Resolve one on a long-lived owner and it sits there, undisposed, for the life of that owner. Awaiten guards against this by default. Where it can see the leak at build time, it reports it; a resolve it cannot check at build time is refused at run time with guidance instead of leaking.

## Strict is the default

In the strict default, a disposable transient is withheld from by-type resolution on the root. On the root, it would accumulate for the whole life of the container.

```csharp expect=AWT219
await using CoffeeShop.Root shop = new();
// highlight-next-line
BrewSession session = shop.Resolve<BrewSession>();   // withheld on the root

[Container]                // strict by default
[Transient<BrewSession>]   // BrewSession is IDisposable
public static partial class CoffeeShop;
```

- On a receiver typed as the generated `Root`, as here, the build warns ([AWT219](../09-diagnostics.md#awt219)).
- At run time the `Resolve` throws with guidance toward a bounded option, and `TryResolve` returns `false`. A resolve through `IAwaitenRoot`, or with a type only known at run time, is checked at run time only.

A transient with an [`OnRelease` hook](../05-registration/02-lifecycle-hooks.md) is withheld the same way, even when [`SuppressDisposal`](./05-disposal.md#suppressing-disposal) opts it out of disposal itself: each construction queues the release on the owner, so on the root the instances pile up just like undisposed ones would, and a rented object would not return to its pool before the container is disposed. Rent it through `Owned<T>` or from a child scope, and the release runs when the handle or the scope is disposed. A `SuppressDisposal` service without a release hook tracks nothing, so it stays resolvable everywhere.

## What is still allowed

Strict mode does not get in your way for the safe shapes:

- `Owned<T>` and `Func<Owned<T>>`, because you control disposal. See [Owned](./06-owned.md).
- A disposable transient injected into a normal constructor, because its owner disposes it.
- A disposable transient resolved from a child scope, because the scope bounds its lifetime.
- A singleton's `Lazy<T>` over a disposable, because it is memoized and bounded.

What strict rejects is the leak-prone shape: a root-owned service (a singleton or a pre-built instance), directly or through the transients it depends on, holds a `Func<T>`, `Func<TArg, T>` or `Func<…, Task<T>>` over a service that is built on demand, and building that service creates a disposable: it is disposable itself, or it builds a disposable transient somewhere below it. Each call would strand more undisposed instances on the root, so this is a non-suppressible error ([AWT118](../09-diagnostics.md#awt118)). Under `Loose` it relaxes to a suppressible warning. Resolve it as `Func<…, Owned<T>>` or `Func<…, Task<Owned<T>>>` instead.

A root-owned service that holds a [dispatch dictionary](../06-resolution/04-keyed-dictionaries.md#dispatch-by-runtime-type) over such a service is reported the same way. A dispatch dictionary has no `Owned` form, so register its holder as scoped, and each scope disposes what the dictionary builds.

AWT118 comes from Awaiten's analyzer, not the generator, so a build that [skips analyzers](../09-diagnostics.md) does not report it.

## Let a consumer own what its Func builds

Some consumers dispose what their `Func<T>` creates, such as a factory that hands each instance to an object that disposes it. List the service in `ConsumerOwned` on the consumer's registration, and the container hands every instance that consumer's `Func` builds over untracked. The consumer's constructor keeps a plain `Func<T>`, and every other consumer of the service keeps container-owned instances.

```csharp
// highlight-next-line
public sealed class Barista(Func<BrewSession> sessions)   // disposes each session it starts
{
    public void Serve()
    {
        using BrewSession session = sessions();
        session.Brew();
    }
}

public sealed class Tasting(BrewSession session)          // the container disposes this session
{
    public void Run() => session.Brew();
}

[Container]
[Transient<BrewSession>]
// highlight-next-line
[Singleton<Barista>(ConsumerOwned = [typeof(BrewSession)])]
[Scoped<Tasting>]
public static partial class CoffeeShop;
```

- It covers the `Func<T>`, `Func<TArg…, T>` and `Func<…, Task<T>>` the consumer takes through its constructor, an injected property or a lifecycle hook, over a transient or parameterized service. List the type the `Func` names, or a generic type definition such as `typeof(Repository<>)` for all its closings.
- For a [dispatch dictionary](../06-resolution/04-keyed-dictionaries.md#dispatch-by-runtime-type), list its base type. The consumer owns its transient entries, while a singleton or scoped entry stays shared.
- The consumer can only dispose the instance it gets, so building the service may leave nothing else on the owner ([AWT261](../09-diagnostics.md#awt261)): no other disposable transient, a decorated instance below the outermost decorator and an injected property included, no `Func` or `Lazy` below it over a transient that creates a disposable, and no `OnRelease` hook. Singleton and scoped dependencies are fine: they belong to their own owner.
- `ConsumerOwned` is set on a consumer's registration, so a decorator or a composite cannot use it.
- An entry that no such `Func` or dictionary of the consumer reaches has no effect and is reported ([AWT262](../09-diagnostics.md#awt262)), for example one over a singleton or scoped service, which the container shares rather than builds per call.
- When several registrations share one implementation, they must agree on `ConsumerOwned` ([AWT166](../09-diagnostics.md#awt166)).

## Loose when you want MS.DI semantics

Set `LifetimeSafety = LifetimeSafety.Loose` to make everything resolvable everywhere, the way Microsoft's container behaves. A disposable transient resolved by type on the root then resolves and is held until the root is disposed, and the leak-prone `Func` pattern becomes a suppressible warning instead of an error.

```csharp
[Container(LifetimeSafety = LifetimeSafety.Loose)]
[Transient<BrewSession>]
public static partial class CoffeeShop;
```

| | `Strict` (default) | `Loose` |
|---|---|---|
| Disposable or release-hooked transient resolved by type on the root | Withheld: [AWT219](../09-diagnostics.md#awt219) on a typed `Root`, throws at run time | Resolves, held until the root is disposed |
| `Func<T>`, `Func<TArg, T>`, `Func<…, Task<T>>` or a dispatch dictionary over one, held by a root-owned service | Non-suppressible error [AWT118](../09-diagnostics.md#awt118) (analyzer only) | Suppressible warning AWT118 |
| `Owned<T>`, a child scope, constructor injection | Allowed | Allowed |

Reach for `Loose` when you are bridging to code that expects MS.DI behavior. Otherwise, strict plus `Owned<T>` keeps disposal honest. A container that [extends](../05-registration/09-extending-containers.md#options) another inherits its setting.

## Where to go next

- [Owned](./06-owned.md) for the recommended way to rent disposables.
- [Disposal](./05-disposal.md) for the teardown rules.
- [MS.DI bridge](../07-hosting/01-msdi-bridge.md) for interop with Microsoft's container.
