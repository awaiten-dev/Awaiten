---
description: Resolve services from a container with Resolve, TryResolve and ResolveAsync, with or without a key.
---

# Resolving services

Once a container is declared, you ask it for services. This page covers the resolve surface. Most of the time you inject dependencies through constructors and never call resolve yourself, but the entry points matter at the edges of your app.

## Resolve

`Resolve<T>()` returns the service or throws if it is not registered.

```csharp
await using CoffeeShop.Root shop = new();

Cup cup = shop.Resolve<Cup>();
```

When the receiver is the generated `Root` or `Scope`, Awaiten knows at compile time which container answers, so it warns about a call that would throw: a service the container does not provide ([AWT218](../09-diagnostics.md#awt218)), or one it does not hand out through that call, such as an async-initialized service on a synchronous `Resolve` ([AWT219](../09-diagnostics.md#awt219)). It also warns about a single service that several imported modules contribute to, because only the order of the imports decides which one the call returns ([AWT155](../09-diagnostics.md#awt155)). This covers the `typeof(...)` and keyed overloads below too. A call through `IAwaitenRoot`, with a type parameter or with a key only known at run time is checked at run time only.

There is a `Type`-based overload for when you do not know the type at compile time.

```csharp
object cup = shop.Resolve(typeof(Cup));
```

A service with [runtime arguments](./06-runtime-arguments.md) resolves only through its factory, because a call by type has no way to pass the arguments. Resolving its bare type, or a `Func<T>`, `Lazy<T>` or `Owned<T>` over it, throws, and the message names the factory to resolve instead, such as `Func<string, Cup>` when `Cup` takes its size as an `[Arg]`.

## TryResolve

`TryResolve<T>(out T?)` returns `false` instead of throwing when the service is not available.

```csharp
if (shop.TryResolve<Cup>(out Cup? cup))
{
    // use cup
}
```

For an async-tainted service in the strict default, `TryResolve` reports `false`, because that service has no synchronous path. See [Async initialization](../04-core-concepts/03-async-initialization.md).

## ResolveAsync

`ResolveAsync<T>(CancellationToken)` constructs the service, awaits its initialization, and returns it ready to use. This is the entry point for async services.

```csharp
EspressoMachine machine = await shop.ResolveAsync<EspressoMachine>(cancellationToken);
```

## Keyed resolution

Each of the three methods has an overload that takes the `Key` a registration was declared with. This is the imperative counterpart to `[FromKey]` at an injection site, and it selects the same registration.

```csharp
IMilk oat = shop.Resolve<IMilk>("Oat");

if (shop.TryResolve<IMilk>(MilkKind.Whole, out IMilk? whole))
{
    // use whole
}

IMilk soy = await shop.ResolveAsync<IMilk>("Soy", cancellationToken);
```

The key is an `object`, so a string, an `enum` constant, or a `typeof(...)` all work, matching the key the registration carries. The `Type` overloads, such as `Resolve(typeof(IMilk), key)`, take an `object?` key, and a `null` key resolves the unkeyed registration, exactly like the keyless overload. The generic overloads declare the key non-nullable, so call the keyless overload there instead. An unknown key follows the same miss rules as above: `Resolve` throws, `TryResolve` reports `false`. See [Keyed services](../05-registration/05-keyed-services.md).

A keyed resolve serves plain services only: it hands out the registration under the key, not a collection of the registrations, a [relationship](./02-relationships.md) over it or the factory of a service with [runtime arguments](./06-runtime-arguments.md). `Resolve<IEnumerable<IMilk>>("Oat")` and `Resolve<Func<IMilk>>("Oat")` throw even when an `IMilk` is registered under `"Oat"`. The container builds these shapes under a key for injection: take them through a `[FromKey("Oat")] IEnumerable<IMilk>` or `[FromKey("Oat")] Func<IMilk>` parameter or property.

## Prefer constructor injection

You rarely call these methods deep in your code. You call them once at the composition root to pull out the top-level service, and everything below is wired by its constructors. A `Barista` that needs a `Grinder` just declares it:

```csharp
public sealed class Barista(Grinder grinder, EspressoMachine machine);
```

Every constructor parameter is a dependency, including one with a C# default value. Awaiten ignores the default, so an unregistered `IMetrics? metrics = null` is [AWT101](../09-diagnostics.md#awt101). For a dependency the service can do without, use an [`Optional` injected property](./05-property-injection.md#tolerate-a-missing-service-with-optional) or a [collection](./03-collections.md), which is empty when nothing registers the service.

For the shapes a dependency can take, like `Func<T>` or collections, see the [table of dependency shapes](./index.md#dependency-shapes).

## Where to go next

- [Relationships](./02-relationships.md) for `Func`, `Lazy`, and `Task`.
- [Collections](./03-collections.md) for many implementations at once.
- [Property injection](./05-property-injection.md) for filling properties instead of constructor parameters.
