---
description: Rent a disposable service with Owned<T> and dispose everything its resolution built when you are done.
---

# Owned

The container disposes what it builds when the scope that owns it closes. `Owned<T>` hands that moment to you: it is the leak-free way to get a disposable service on demand and decide when it goes away. You rent a brew session, use it, and return it. When you dispose the handle, everything that resolution built is disposed with it, while shared singletons live on.

## Rent a service

Resolve `Owned<T>`, use its `Value`, and dispose the handle when done.

```csharp
using (Owned<BrewSession> rented = shop.Resolve<Owned<BrewSession>>())
{
    rented.Value.Brew();
} // the brew session and everything it built is disposed here
```

## Rent many

Inject `Func<Owned<T>>` to rent repeatedly. Each handle owns its own throwaway scope, so disposing one does not affect another.

```csharp
public sealed class Counter(Func<Owned<BrewSession>> sessions)
{
    public void Serve()
    {
        using Owned<BrewSession> session = sessions();
        session.Value.Brew();
    }
}
```

Because each handle has a scope of its own, `Func<Owned<T>>` is also how a singleton reaches a fresh scoped service on each use, see [Relationships](../06-resolution/02-relationships.md#reaching-shorter-lived-services).

## Rent with an argument

Combine it with a runtime argument. See [Runtime arguments](../06-resolution/06-runtime-arguments.md).

```csharp
public sealed class Counter(Func<string, Owned<Cup>> cups)
{
    public void Serve(string size)
    {
        using Owned<Cup> cup = cups(size);
        // ...
    }
}
```

## What disposal covers

Disposing the handle releases the dedicated scope that built the service, including its transitive disposables. It never disposes the container root, and it never disposes a shared singleton the service depended on.

## Rent an async-initialized service

For an async-initialized service, or one that depends on one, inject `Func<Task<Owned<T>>>`, or `Task<Owned<T>>` for a single handle. The task completes once the service is initialized. Dispose the handle with `await using`, so an async-disposable service is torn down through `DisposeAsync`.

```csharp
public sealed class MilkSteamer : IAsyncDisposable
{
    public void Steam() { }
    public ValueTask DisposeAsync() => default;
}

public sealed class Counter(Func<Task<Owned<MilkSteamer>>> steamers)
{
    public async Task ServeAsync()
    {
        // highlight-next-line
        await using Owned<MilkSteamer> steamer = await steamers();
        steamer.Value.Steam();
    }
}
```

## Rules

- A synchronous `Owned<T>` or `Func<Owned<T>>` cannot await initialization, so over an async-tainted service it is [AWT119](../09-diagnostics.md#awt119) or [AWT120](../09-diagnostics.md#awt120). Use the `Task` forms above.
- An `Owned<T>` handle cannot be requested through a `Lazy<Owned<T>>` ([AWT121](../09-diagnostics.md#awt121)), since memoizing one handle would hand back a disposed one after the first teardown.
- A service built by a factory with a `[RequestingType]` parameter has no `Owned<T>` form ([AWT186](../09-diagnostics.md#awt186)).
- Only the container creates `Owned<T>`. A handle built with `new` disposes whatever scope it is given, the root included, so constructing one in your code is a warning ([AWT205](../09-diagnostics.md#awt205)).

## Where to go next

- [Lifetime safety](./04-lifetime-safety.md) for why disposable transients are withheld from the root.
- [Runtime arguments](../06-resolution/06-runtime-arguments.md) for building with values you pass in.
- [Factories and instances](../05-registration/01-factories-and-instances.md) to start telling the container how to build a service.
