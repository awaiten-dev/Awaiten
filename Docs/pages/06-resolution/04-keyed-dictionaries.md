---
description: Resolve every keyed implementation of a service as a dictionary by key, including dispatch by runtime type.
---

# Keyed dictionaries

A keyed dictionary hands you every keyed implementation of a service, mapped by its key. It is the milk fridge as a whole. Ask for the fridge and you can reach for oat, whole, or soy by name at runtime.

## Resolve the map

Register the implementations with keys, then depend on `IReadOnlyDictionary<string, TService>`.

```csharp
[Container]
[Singleton<OatMilk, IMilk>(Key = "Oat")]
[Singleton<WholeMilk, IMilk>(Key = "Whole")]
[Singleton<SoyMilk, IMilk>(Key = "Soy")]
public static partial class CoffeeShop;

public sealed class MilkFridge(IReadOnlyDictionary<string, IMilk> milks)
{
    public IMilk Get(string kind) => milks[kind];
}
```

You can also resolve it directly:

```csharp
IReadOnlyDictionary<string, IMilk> milks = shop.Resolve<IReadOnlyDictionary<string, IMilk>>();
IMilk oat = milks["Oat"];
```

## What goes in

The dictionary contains every keyed registration of the service, keyed by its `Key`. Unkeyed registrations are left out. Each member keeps its own lifetime. With no keyed registrations, you get an empty dictionary.

## The key type: string or enum

Awaiten synthesizes the dictionary for a `string` key or an `enum` key. For an enum-keyed map, register every implementation under a constant of the same enum and depend on `IReadOnlyDictionary<TEnum, TService>`:

```csharp
public enum MilkKind { Oat, Whole, Soy }

[Container]
[Singleton<OatMilk, IMilk>(Key = MilkKind.Oat)]
[Singleton<WholeMilk, IMilk>(Key = MilkKind.Whole)]
public static partial class CoffeeShop;

public sealed class MilkFridge(IReadOnlyDictionary<MilkKind, IMilk> milks);
```

The rules:

- The dictionary synthesizes only when every keyed registration of the service matches the requested key type. A key type that is neither `string` nor an enum, or registrations that mix key kinds, is a build error ([AWT159](../09-diagnostics.md#awt159)) unless you register the dictionary yourself.
- Registrations keyed with `typeof(...)` cannot be collected into an `IReadOnlyDictionary<Type, TService>`. A `Type` key builds only a [dispatch dictionary](#dispatch-by-runtime-type), whose value is a `Func<TBase>`; any other value type is a build error ([AWT213](../09-diagnostics.md#awt213)).
- A synthesized dictionary already resolves every key, so applying `[FromKey]` to it is an error ([AWT160](../09-diagnostics.md#awt160)).
- The dictionary builds every member while the consumer is built, so it cannot hold an async-initialized member ([AWT122](../09-diagnostics.md#awt122)). Use the async form below.

## The async form

For async members, ask for `Task<IReadOnlyDictionary<string, TService>>`. It awaits every member and keeps the consumer synchronously resolvable. If all members are synchronous, you get a completed task.

## Dispatch by runtime type

Sometimes the implementation to use depends on a type that is only known at run time. The classic case is a mediator: it receives an `object` message and needs the `IOrderHandler<T>` for `message.GetType()`. Key a dictionary by `Type` and Awaiten maps the type argument of each closing of the generic handler to a factory for that closing:

```csharp
public interface IOrderHandler { string Handle(object message); }
public interface IOrderHandler<T> : IOrderHandler;

public sealed class OrderPlacedHandler : IOrderHandler<OrderPlaced> { public string Handle(object message) => "placed"; }
public sealed class OrderPaidHandler : IOrderHandler<OrderPaid> { public string Handle(object message) => "paid"; }

[Container]
[Scan(typeof(IOrderHandler<>), As = ScanAs.Marker)]
[Singleton<OrderRouter>]
public static partial class CoffeeShop;

public sealed class OrderRouter(IReadOnlyDictionary<Type, Func<IOrderHandler>> handlers)
{
    public string Handle(object message) => handlers[message.GetType()]().Handle(message);
}
```

The dictionary holds one entry per closing, `typeof(OrderPlaced)` and `typeof(OrderPaid)` here, and the scan fixes that set at compile time. The generated code is a plain dictionary of `Func` delegates, with no reflection, so it stays trimming and native-AOT safe. You do not need to inject a resolver and suppress [AWT135](../09-diagnostics.md#awt135) for this: the dictionary is the checked way to dispatch by runtime type.

The rules:

- **The value is `Func<TBase>`.** `TBase` is a non-generic interface that the generic interface extends, like `IOrderHandler` above. Each value resolves its closing exactly like a `Func<IOrderHandler<OrderPlaced>>` parameter would: with the closing's own lifetime, through its decorators, and from the scope that built the consumer.
- **The members are the closed registrations.** Every unkeyed registration of a closed `IOrderHandler<T>` is an entry, whether a [scan](../05-registration/07-scanning.md) match (with `As = ScanAs.Marker`, so the match is registered under its closed interface) or an explicit one such as `[Transient<OrderPlacedHandler, IOrderHandler<OrderPlaced>>]`. An [open generic](../05-registration/06-open-generics.md) registration adds no entry, because its closings are unbounded.
- **No key.** The dictionary holds every unkeyed closing, so a `[FromKey]` on it is a build error ([AWT213](../09-diagnostics.md#awt213)).
- **One type parameter, one generic interface.** The key is the single type argument. A closing with two type arguments, or a second generic interface that also extends `TBase`, is a build error ([AWT213](../09-diagnostics.md#awt213)). Give each generic interface its own base interface.
- **One implementation per type.** When two implementations close the same `T`, the entry resolves the one a direct `IOrderHandler<T>` dependency would get, so an explicit registration overrides a scanned handler.
- **Exact types.** The lookup matches the exact runtime type, not a base type. An unknown type behaves like any dictionary: the indexer throws `KeyNotFoundException`, and `TryGetValue` returns `false`.
- **Synchronous handlers.** Each value is a synchronous `Func`, so an async-initialized handler is reported like any `Func` over one ([AWT119](../09-diagnostics.md#awt119)).
- **Lifetime safety.** A singleton consumer that reaches a disposable transient handler through the dictionary would strand each instance on the root ([AWT118](../09-diagnostics.md#awt118)). Register the consumer as `Scoped` so each scope disposes the handlers it builds.

Inject the dictionary into a constructor, a factory, or an [injected property](./05-property-injection.md). It is not offered by `Resolve<T>`.

:::tip[When not to reach for this]
Reach for the dispatch dictionary only when the type really is unknown until run time, like a message arriving as `object`. When the caller knows the type, inject `IOrderHandler<OrderPlaced>` directly, so the compiler checks that a handler exists. When every handler of a message should run, inject a [collection](./03-collections.md) of `IOrderHandler<OrderPlaced>` instead.
:::

## Where to go next

- [Keyed services](../05-registration/05-keyed-services.md) to select a single keyed implementation.
- [Collections](./03-collections.md) for all unkeyed implementations.
