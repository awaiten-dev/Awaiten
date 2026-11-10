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
public sealed class Register(Lazy<MenuBoard> menu)
{
    public string Today() => menu.Value.Special;   // built on first read
}
```

## Task: the async shape

`Task<T>` is how a synchronous service holds an async one. See [Async initialization](../03-core-concepts/03-async-initialization.md) for the full story.

```csharp
public sealed class Barista(Task<EspressoMachine> machine)
{
    public async Task<Cup> PullAsync() => (await machine).Pull();
}
```

## Resolve a relationship directly

The shapes you inject, you can also resolve by type, from the root or from a scope. `Func<T>`, `Lazy<T>` and `Owned<T>` come with every service, with two exceptions: a service with [runtime arguments](./06-runtime-arguments.md) comes only as its factories, such as `Func<TArg..., T>` and `Func<TArg..., Owned<T>>`, and a service built by a factory with a `[RequestingType]` parameter has no `Owned<T>` ([AWT186](../09-diagnostics.md#awt186)). The async shapes `Task<T>`, `Func<Task<T>>`, `Lazy<Task<T>>`, `Task<Owned<T>>` and `Func<Task<Owned<T>>>` come with a service that is initialized asynchronously, which is where you need them.

```csharp
Func<Task<EspressoMachine>> machines = shop.Resolve<Func<Task<EspressoMachine>>>();
EspressoMachine machine = await machines();
```

A synchronous shape over an async-initialized service cannot await the initialization, so resolving it throws, and the message names the async shape to use instead. An async shape over a synchronous service throws too, and its message tells you to resolve the service directly, or through `ResolveAsync` if you want a task.

## Relationships wrap a single service

A relationship wraps one service, not a [collection](./03-collections.md). A dependency on `Func<IEnumerable<ISyrup>>`, `Lazy<IReadOnlyList<ISyrup>>` or `Owned<ISyrup[]>` is an error ([AWT256](../09-diagnostics.md#awt256)), and so is a collection of relationships, such as `IEnumerable<Lazy<ISyrup>>` ([AWT254](../09-diagnostics.md#awt254)). Depend on the collection itself. To defer or own the members, register a small service that takes the collection and depend on the relationship over that service.

## Relationships bind to their owner

A relationship resolves from the scope that built the consumer, not from wherever it is later called. A singleton that holds a `Func<Order>` resolves that `Order` from the root, always. It is never rebound to some child scope that happens to call it later. This keeps lifetimes predictable.

## Reaching shorter-lived services

Relationships are the clean way for a long-lived service to reach a shorter-lived one. A singleton cannot depend on a scoped service directly ([AWT105](../09-diagnostics.md#awt105)), but it can hold a `Func<T>` or `Lazy<T>` and pull it when needed.

:::warning
In the strict default, a root-owned `Func<T>` over a disposable transient is withheld, because it would pile up undisposed instances on the root. Use [Owned](../06-lifetime/01-owned.md) instead, or switch to [Loose lifetime safety](../06-lifetime/02-lifetime-safety.md).
:::

## Calling a relationship while the service is built

A `Func<T>` or `Lazy<T>` of a singleton or scoped service returns the one instance the container caches, and the container caches it only once its constructor and its `OnActivated` hook have returned. Calling it before then, from the service's own constructor, a decorator of it, a dependency it builds or its `OnActivated` hook, throws an `InvalidOperationException` instead of building a second instance. Call the `Func` or `Lazy` after the constructor returns, or take the service through a [deferred property](./05-property-injection.md#break-a-cycle-with-deferred), which the container wires once it has cached the owner. A service with a deferred property of its own is cached as soon as its constructor returns, so its deferred properties and its `OnActivated` hook already get the cached instance.

:::warning
Some loops the container cannot stop. A transient that resolves itself is plain recursion: every call builds a new instance, so it runs until the stack overflows. The same holds for a scoped service that resolves an `Owned<T>` of itself, since each handle builds it in a new scope. An async service that awaits a `Task<T>` of itself after its first `await` waits for itself and never completes. A constructor that resolves its own service on another thread and waits for the result deadlocks, because that thread waits for the build in progress.
:::

## Where to go next

- [Runtime arguments](./06-runtime-arguments.md) for `Func<TArg, T>` with values you pass in.
- [Owned](../06-lifetime/01-owned.md) for disposable services you rent and return.
