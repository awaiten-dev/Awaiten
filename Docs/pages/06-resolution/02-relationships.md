---
description: Depend on Func<T>, Lazy<T> or Task<T> to get a service on demand, once on first use, or asynchronously.
---

# Relationships

A dependency does not have to be the service itself. It can be a way to get the service later, or more than once. These wrapper shapes are called relationships. A barista does not hold a hundred cups. It holds a way to make a cup when an order comes in.

## Func: build on demand

Ask for `Func<T>` to get a service each time you call it. A transient gives you a fresh one per call. A singleton gives you the same one.

```csharp
public sealed class Barista(Func<Cup> cups)
{
    public Cup ServeNext() => cups();   // a fresh cup every order
}
```

## Lazy: build once, on first use

`Lazy<T>` defers construction until the first access, then memoizes it. Good for something expensive that might not be needed.

```csharp
public sealed class MenuDisplay(Lazy<MenuBoard> menu)
{
    public string Today() => menu.Value.Special;   // built on first read
}
```

## Task: the async shape

`Task<T>` is how a synchronous service holds an async one. See [Async initialization](../04-core-concepts/03-async-initialization.md) for the full story.

```csharp
public sealed class Barista(Task<EspressoMachine> machine)
{
    public async Task<Cup> PullAsync() => (await machine).Pull();
}
```

## Resolve a relationship directly

The shapes you inject, you can also resolve by type, from the root or from a scope. `Func<T>`, `Lazy<T>` and `Owned<T>` come with every service, with three exceptions:

- An [async-tainted](../12-reference/03-glossary.md#async-tainted) service: its synchronous shapes throw, as the paragraph after the example explains.
- A service with [runtime arguments](./06-runtime-arguments.md) comes only as its factories, such as `Func<TArg…, T>` and `Func<TArg…, Owned<T>>`.
- A service built by a factory with a `[RequestingType]` parameter has no `Owned<T>` ([AWT186](../09-diagnostics.md#awt186)).

The async shapes `Task<T>`, `Func<Task<T>>`, `Lazy<Task<T>>`, `Task<Owned<T>>` and `Func<Task<Owned<T>>>` come with an async-tainted service, which is where you need them. A service with runtime arguments comes with `Func<TArg…, Task<T>>` and `Func<TArg…, Task<Owned<T>>>`, async-tainted or not.

```csharp
Func<Task<EspressoMachine>> machines = shop.Resolve<Func<Task<EspressoMachine>>>();
EspressoMachine machine = await machines();
```

A synchronous shape over an async-tainted service cannot await the initialization, so resolving it throws, and the message names the async shape to use instead. An async shape over a synchronous service throws too, and its message tells you to resolve the service directly, or through `ResolveAsync` if you want a task. A `Func<T>`, `Lazy<T>` or `Owned<T>` over a service with runtime arguments throws with its factory as the remedy.

Resolving a relationship directly works without a key only. A [keyed resolve](./01-resolving-services.md#keyed-resolution) serves plain services, so `Resolve<Func<IMilk>>("Oat")` throws even when an `IMilk` is registered under `"Oat"`. Inject the keyed relationship instead, through a `[FromKey("Oat")] Func<IMilk>` parameter or property.

## Relationships are built, not registered

The container builds every relationship it injects, over the service the relationship wraps. Registering a relationship type as a service of its own, such as `[Singleton<Func<Cup>>(Factory = ...)]`, does not change that for a single relationship dependency: a `Func<Cup>` parameter still gets the relationship over `Cup` and never the registered `Func<Cup>`, so the dependency is an error ([AWT259](../09-diagnostics.md#awt259)). The same holds for `Lazy<T>`, `Owned<T>`, `Task<T>` and `Func<TArg…, T>`, with or without a key. A collection of the registered type, such as `IEnumerable<Func<Cup>>`, does hold the registration, see [Relationships wrap a single service](#relationships-wrap-a-single-service).

Register the service and let the container build the relationship. When the factory does more than build the service, give it a name: register a small interface and depend on that instead.

```csharp
public interface ICupDispenser
{
    Cup Next();
}

public sealed class CupDispenser : ICupDispenser
{
    public Cup Next() => new();
}

public sealed class CupStation(ICupDispenser cups)
{
    public Cup ServeNext() => cups.Next();
}

[Container]
[Singleton<CupDispenser, ICupDispenser>]
[Singleton<CupStation>]
public static partial class CoffeeShop;
```

## Relationships wrap a single service

A relationship wraps one service, not a [collection](./03-collections.md) or another relationship, so these are errors:

- A relationship over a collection, such as `Func<IEnumerable<ISyrup>>`, `Lazy<IReadOnlyList<ISyrup>>` or `Owned<ISyrup[]>` ([AWT256](../09-diagnostics.md#awt256)), unless the collection type itself is [registered as a service](./03-collections.md#registering-your-own-collection).
- A collection of relationships, such as `IEnumerable<Lazy<ISyrup>>` or `Func<ISyrup>[]` ([AWT254](../09-diagnostics.md#awt254)), unless the relationship type itself is registered as a service. Then the collection holds that registration.
- A relationship over another relationship, such as `Func<Lazy<ISyrup>>`, `Lazy<Func<ISyrup>>`, `Func<TArg…, Lazy<ISyrup>>` or `Task<Lazy<ISyrup>>`. The container builds only the combinations in the [dependency shapes](./index.md#dependency-shapes) table, such as `Func<Owned<T>>` or `Lazy<Task<T>>`, and reports any other as a missing dependency ([AWT101](../09-diagnostics.md#awt101)).

Depend on the collection itself. To defer or own the members, register a small service that takes the collection and depend on the relationship over that service.

## Relationships bind to their owner

A relationship resolves from the scope that built the consumer, not from wherever it is later called. A singleton that holds a `Func<Order>` resolves that `Order` from the root, always. It is never rebound to some child scope that happens to call it later. This keeps lifetimes predictable.

## Reaching shorter-lived services

A singleton cannot depend on a scoped service directly ([AWT105](../09-diagnostics.md#awt105)). A `Func<Order>` or `Lazy<Order>` in a singleton compiles without a diagnostic, but it does not reach a shorter lifetime: because relationships bind to their owner, it resolves from the root and always returns the root's one `Order`, which lives as long as the container. To give a long-lived service a fresh scoped service on each use, take `Func<Owned<T>>`. Each call builds the service in a scope of its own, and disposing the handle disposes that scope. See [Owned](../04-core-concepts/06-owned.md).

```csharp
// highlight-next-line
public sealed class OrderTaker(Func<Owned<Order>> orders)
{
    public void Take()
    {
        using Owned<Order> order = orders();   // a new order, disposed with the handle
    }
}

[Container]
[Singleton<OrderTaker>]
[Scoped<Order>]
public static partial class CoffeeShop;
```

A `Func<T>` or `Lazy<T>` is the right shape for building a transient or a singleton on demand, not for reaching a shorter lifetime. One case needs `Func<Owned<T>>` instead: a root-owned service, such as a singleton, that holds a `Func<T>` over a transient whose construction creates a disposable, the transient itself or one of its dependencies. Every call would track a new disposable on the root until the container is disposed, so it is reported ([AWT118](../09-diagnostics.md#awt118)): an error in the strict default, a warning under [Loose lifetime safety](../04-core-concepts/04-lifetime-safety.md).

Resolving such a `Func<T>` by type from the root throws in the strict default for the same reason. Resolve it from a scope, whose disposal bounds what it builds, or resolve `Func<Owned<T>>`.

## Calling a relationship while the service is built

A `Func<T>` or `Lazy<T>` of a singleton or scoped service returns the one instance the container caches, and the container caches it only once its constructor and its `OnActivated` hook have returned. Calling it before then, from the service's own constructor, a decorator of it, a dependency it builds or its `OnActivated` hook, throws an `InvalidOperationException` instead of building a second instance. Call the `Func` or `Lazy` after the constructor returns, or take the service through a [deferred property](./05-property-injection.md#break-a-cycle-with-deferred), which the container wires once it has cached the owner. A service with a deferred property of its own is cached as soon as its constructor returns, so its deferred properties and its `OnActivated` hook already get the cached instance.

:::warning
Some loops the container cannot stop. A transient that resolves itself is plain recursion: every call builds a new instance, so it runs until the stack overflows. The same holds for a scoped service that resolves an `Owned<T>` of itself, since each handle builds it in a new scope. An async service that awaits a `Task<T>` of itself after its first `await` waits for itself and never completes. A constructor that resolves its own service on another thread and waits for the result deadlocks, because that thread waits for the build in progress.
:::

## Where to go next

- [Runtime arguments](./06-runtime-arguments.md) for `Func<TArg, T>` with values you pass in.
- [Owned](../04-core-concepts/06-owned.md) for disposable services you rent and return.
