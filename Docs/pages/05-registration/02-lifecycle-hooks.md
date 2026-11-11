---
description: Run container-side code when an instance is activated or released, synchronously or asynchronously.
---

# Lifecycle hooks

Like a factory, a lifecycle hook is a method on the container. It lets you run code when an instance is activated and when it is released, without putting that code in the service itself. Calibrate the espresso machine when it comes online. Purge it when the shop closes.

## OnActivated and OnRelease

Name a hook method with `OnActivated` or `OnRelease`. The method lives on the container and takes the instance. Declare the parameter as the implementation type and Awaiten hands it over directly, no cast needed.

```csharp
[Container]
[Singleton<EspressoMachine>(OnActivated = nameof(Calibrate), OnRelease = nameof(Purge))]
public static partial class CoffeeShop
{
    private static void Calibrate(EspressoMachine machine) => machine.Calibrate();
    private static void Purge(EspressoMachine machine) => machine.Purge();
}
```

You can type the parameter as `object` instead when one hook serves several implementations.

`OnActivated` runs once, right after construction. For an async service it runs before the async initializer conventions. `OnRelease` runs when the owner or scope is disposed, before the instance's own `Dispose`, and in reverse creation order. By default it runs *in addition to* that disposal; pair it with [`SuppressDisposal`](../04-core-concepts/05-disposal.md#suppressing-disposal) to replace disposal entirely, as when a hook returns an object to a pool.

## Hook parameters

Both hooks may declare parameters after the instance. The first parameter is always the instance; every parameter after it is resolved from the container graph, exactly like a constructor or factory parameter. Calibrate the machine from injected settings; return a pooled buffer to the pool it came from.

```csharp
[Container]
[Singleton<Settings>]
[Singleton<BufferPool>]
[Singleton<EspressoMachine>(OnActivated = nameof(Calibrate))]
[Transient<Buffer>(Factory = nameof(Rent), OnRelease = nameof(ReturnToPool))]
public static partial class CoffeeShop
{
    private static void Calibrate(EspressoMachine machine, Settings settings) => machine.Calibrate(settings);
    private static Buffer Rent(BufferPool pool) => pool.Rent();
    private static void ReturnToPool(Buffer buffer, BufferPool pool) => pool.Return(buffer);
}
```

An activation dependency is resolved inline as the hook is called. A release dependency is resolved at construction and captured by value into the queued closure, so the hook holds the instance it was queued for even if a later resolve fails and rolls back. Reverse creation-order teardown keeps a captured dependency (a singleton pool, say) alive until after the release that uses it has run.

Hook parameters participate in the graph like any other dependency, including cycle, captive-dependency and async-taint analysis. The rules:

- An unregistered hook parameter is a missing dependency ([AWT101](../09-diagnostics.md#awt101)).
- A hook parameter cannot be a runtime `[Arg]`, because no `Func<…>` call supplies one ([AWT189](../09-diagnostics.md#awt189)).
- A release hook parameter cannot be a `Func` or `Lazy` relationship, synchronous or async, or a [dispatch dictionary](../06-resolution/04-keyed-dictionaries.md#dispatch-by-runtime-type) ([AWT191](../09-diagnostics.md#awt191)). Those resolve when called, and by the time the release hook runs the owner is already tearing down.
- An activation hook may take a `Func<T>` or `Lazy<T>`, but calling one over the singleton or scoped service it activates throws while that service is not cached yet, see [Relationships](../06-resolution/02-relationships.md#calling-a-relationship-while-the-service-is-built).

A C# default value does not make a hook parameter optional ([AWT101](../09-diagnostics.md#awt101), see [Prefer constructor injection](../06-resolution/01-resolving-services.md#prefer-constructor-injection)). To let the hook run without the service, take a collection of it, which is empty when nothing registers the service. A relationship such as `Func<T>` or `Lazy<T>` does not help, because it needs the service registered too. When the service itself uses the dependency, give the service an [`Optional` injected property](../06-resolution/05-property-injection.md#tolerate-a-missing-service-with-optional) instead of passing the dependency through the hook.

```csharp
[Container]
[Singleton<EspressoMachine>(OnActivated = nameof(Calibrate))]
public static partial class CoffeeShop
{
    private static void Calibrate(EspressoMachine machine, IEnumerable<Settings> settings)
    {
        if (settings.FirstOrDefault() is { } shopSettings)
        {
            machine.Calibrate(shopSettings);
        }
    }
}
```

## Asynchronous activation

An `OnActivated` hook may return `Task` or `ValueTask`. The container awaits it, so the service becomes [async-initialized](../04-core-concepts/03-async-initialization.md#use-an-activation-hook), like a service an async initializer convention matches. Connect the payment terminal without putting Awaiten on it.

```csharp
[Container]
[Singleton<Settings>]
[Singleton<PaymentTerminal, IPaymentTerminal>(OnActivated = nameof(ConnectAsync))]
public static partial class CoffeeShop
{
    private static Task ConnectAsync(PaymentTerminal terminal, Settings settings, CancellationToken cancellationToken)
        => terminal.HandshakeAsync(cancellationToken);
}
```

A `CancellationToken` parameter receives the token passed to `ResolveAsync`, `InitializeAsync` or `CreateScopeAsync`. Every other parameter after the instance resolves from the graph, as for a synchronous hook.

The service and everything that depends on it now resolve through `ResolveAsync`. All async rules apply: a synchronous `Func<T>` or `Lazy<T>` over it is [AWT119](../09-diagnostics.md#awt119), an `Eager` singleton needs `AsyncSafety.SyncAfterInit` ([AWT161](../09-diagnostics.md#awt161)), and the MS.DI bridge exposes it as `Task<T>`.

The hook works on every registration the container owns: any lifetime, keyed, `Factory`, open generic, scan and module registrations.

Only `Task` and `ValueTask` are awaited. A hook that returns `Task<T>` or `ValueTask<T>` is [AWT232](../09-diagnostics.md#awt232), because the container has no use for the result.

## Asynchronous release

An `OnRelease` hook may return `Task` or `ValueTask` too. Use it for cleanup that belongs to the container, not to the service, such as flushing a shared sink or returning an object to a pool. The service itself stays free of Awaiten.

```csharp
[Container]
[Singleton<ReceiptPrinter>]
[Scoped<Order>(OnRelease = nameof(PrintReceiptAsync))]
public static partial class CoffeeShop
{
    private static Task PrintReceiptAsync(Order order, ReceiptPrinter printer)
        => printer.PrintAsync(order);
}
```

`DisposeAsync` awaits the hook. It keeps the place a synchronous hook has: in reverse creation order, before any instance is disposed. Release dependencies are captured at construction, as for a synchronous hook. `DisposeAsync` has no `CancellationToken`, so the hook cannot take one ([AWT232](../09-diagnostics.md#awt232)).

The hook does not make the service async-initialized. It still resolves through `Resolve`, because the hook only runs at teardown.

Dispose the owner with `await using` or `DisposeAsync`. A synchronous `Dispose` cannot await the hook, so it skips it and throws once the rest is torn down, as described in [Disposal](../04-core-concepts/05-disposal.md#a-synchronous-dispose-of-async-only-teardown) ([AWT156](../09-diagnostics.md#awt156)). The same holds for an [`Owned<T>`](../04-core-concepts/06-owned.md) handle and for a scope of the [MS.DI bridge](../07-hosting/01-msdi-bridge.md).

## Ordering and failure

Activation happens before the instance is ever handed out, so a concurrent resolve never sees a service that has not been activated. The hook runs first, synchronous or asynchronous. Then the container awaits each [async initializer convention](../04-core-concepts/03-async-initialization.md#order-of-several-initializers) the service matches, in declaration order. If `OnActivated` or a convention throws or its task faults, the instance is not published, and a disposable instance is disposed. A later resolve builds a fresh one and tries again. Release is only queued once activation succeeds.

If `OnRelease` throws or its task faults, the teardown goes on: the other release hooks still run, and every instance is still disposed, including the one whose hook failed. The failure surfaces once the teardown is complete, together with any other teardown failure. See [When teardown fails](../04-core-concepts/05-disposal.md#when-teardown-fails).

## Hooks on scans and modules

A `[Scan]` can name the same `OnActivated`/`OnRelease` hooks, applied to every match, so a whole scanned family shares one routine. See [Scanning](./07-scanning.md#lifecycle-hooks).

A hook of a [module](./08-modules/index.md) registration or scan may stay `internal`. When a container in another assembly imports the module, the [library module rules](./08-modules/02-library-module-rules.md#hooks) apply.

## Eager singletons

`Eager = true` builds a singleton when the root is created. It is a lifetime option, so it is described under [Lifetimes](../04-core-concepts/01-lifetimes.md#build-a-singleton-at-startup).

## Rules

- Hooks are for services the container owns. Setting one on a pre-built `Instance` registration is an error ([AWT165](../09-diagnostics.md#awt165)).
- Registrations of the same implementation share one instance, so they must agree on its hooks and on `Eager`. Two registrations that name different hooks, or one with `Eager` and one explicitly without, are an error ([AWT166](../09-diagnostics.md#awt166)), and so is a scan that names a hook for a match whose registration names another or none. A registration that leaves a hook unset takes the other's.

## Where to go next

- [Decorators](./03-decorators.md) to wrap a service in container-side behavior.
- [Async initialization](../04-core-concepts/03-async-initialization.md) for async initializer conventions.
- [Disposal](../04-core-concepts/05-disposal.md) for teardown order.
