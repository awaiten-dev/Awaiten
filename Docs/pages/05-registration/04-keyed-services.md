---
description: Register several implementations of one service under keys and choose one per consumer.
---

# Keyed services

Sometimes one service type has several implementations and you want to pick a specific one by name. The milk fridge holds oat, whole, and soy. They are all `IMilk`, but a recipe asks for one in particular. Keys let you register them side by side, and the container picks the right one for each consumer, so the consumer stays a plain class.

## Register with a key

Give each registration a `Key`.

```csharp
[Container]
[Singleton<OatMilk, IMilk>(Key = "Oat")]
[Singleton<WholeMilk, IMilk>(Key = "Whole")]
[Singleton<SoyMilk, IMilk>(Key = "Soy")]
public static partial class CoffeeShop;
```

A plain `Resolve<IMilk>()` does not return a keyed registration. You can mix a keyed set with one unkeyed registration, and the unkeyed one is what a plain resolve gives you.

### Typed keys

A key can be an `enum` constant (or a `typeof(...)`) instead of a string. An enum key refactors safely, makes the valid keys discoverable, and lets the C# compiler catch a typo even in a `Resolve(key)` call through `IAwaitenRoot`, which Awaiten can only check at run time. On the generated `Root` or `Scope`, Awaiten also warns about a constant key nothing is registered under ([AWT218](../09-diagnostics.md#awt218)). At an injection site, Awaiten checks every key, string or enum: a `[FromKey]` that names a key nothing is registered under is a missing dependency ([AWT101](../09-diagnostics.md#awt101)).

```csharp
public enum MilkKind { Oat, Whole, Soy }

[Container]
[Singleton<OatMilk, IMilk>(Key = MilkKind.Oat)]
[Singleton<WholeMilk, IMilk>(Key = MilkKind.Whole)]
public static partial class CoffeeShop;
```

Keys of different kinds stay distinct: the enum `MilkKind.Oat` and the string `"Oat"` are two different keys.

## Choose how a consumer picks a variant

There are three ways to give a consumer one particular implementation. The first two keep the consumer a plain class, because the selection lives on the container.

| Form | Use it when | Domain type stays free of Awaiten |
|---|---|---|
| [`WhenInjectedInto`](#bind-a-variant-to-a-consumer-with-wheninjectedinto) | a consumer always wants one variant | yes |
| [A factory with `[FromKey]`](#disambiguate-same-typed-dependencies-with-a-factory) | a consumer takes two dependencies of one type under different keys | yes |
| [`[FromKey]` on the parameter](#select-directly-with-fromkey) | you would rather annotate the constructor than add a factory | no |

## Bind a variant to a consumer with `WhenInjectedInto`

When a consumer should always get one particular implementation, bind it at the container with `WhenInjectedInto` and point it at the consumer type. The consumer's constructor just asks for the bare service type; the container decides which implementation it gets.

```csharp
public interface IReceiptPrinter;
public sealed class ThermalPrinter : IReceiptPrinter;
public sealed class WidePrinter : IReceiptPrinter;

public sealed class CounterRegister(IReceiptPrinter printer);
public sealed class DriveThroughRegister(IReceiptPrinter printer);

[Container]
[Singleton<ThermalPrinter, IReceiptPrinter>]
[Singleton<WidePrinter, IReceiptPrinter>(WhenInjectedInto = typeof(DriveThroughRegister))]
[Singleton<CounterRegister>]
[Singleton<DriveThroughRegister>]
public static partial class CoffeeShop;
```

`DriveThroughRegister` gets the `WidePrinter`, and its constructor still just asks for `IReceiptPrinter`. Every other consumer, and the public resolution, get the unconditional `ThermalPrinter`. The contextual registration is private to its consumer, so it is left out of the public collection too.

```csharp
DriveThroughRegister driveThrough = shop.Resolve<DriveThroughRegister>();   // built with the WidePrinter
CounterRegister counter = shop.Resolve<CounterRegister>();                  // built with the ThermalPrinter
IReceiptPrinter printer = shop.Resolve<IReceiptPrinter>();                  // ThermalPrinter
```

A contextual binding fills a property injected by [`[InjectProperty<T>]`](../06-resolution/05-property-injection.md) the same way it fills a constructor parameter, and a decorator on the service wraps the contextual implementation along with the default. The consumer may also be an implementation an imported [module](./07-modules/index.md) registers or scans: the binding reaches its constructor parameters, `[Inject]` properties and lifecycle hook parameters, also when a module of another assembly builds it through a generated factory.

The consumer may be a [decorator](./02-decorators.md) or a [composite](./03-composites.md) too. The binding reaches the dependencies it takes besides the instance it wraps or the collection it fans out over, in every chain it wraps and under every key. It never replaces that inner instance or collection. For an open generic decorator, name the closing the binding applies to, such as `typeof(LoggingBehavior<PlaceOrder>)`, as for any open generic consumer.

:::note
If the named consumer has no unkeyed constructor parameter of the service to redirect, the binding never applies, and Awaiten warns ([AWT167](../09-diagnostics.md#awt167)).
:::

## Disambiguate same-typed dependencies with a factory

`WhenInjectedInto` redirects a single parameter, so it cannot express a consumer that needs *two* of the same type under different keys. Build that type through a [factory method](./01-factories-and-instances.md) whose parameters carry the keys, so the disambiguation lives on the factory and the type stays a plain class.

```csharp
public sealed class Blend(IMilk primary, IMilk secondary);   // a plain class: two same-typed params, no attributes

[Container]
[Singleton<OatMilk, IMilk>(Key = "Oat")]
[Singleton<SoyMilk, IMilk>(Key = "Soy")]
[Transient<Blend>(Factory = nameof(MakeBlend))]
public static partial class CoffeeShop
{
    private static Blend MakeBlend([FromKey("Oat")] IMilk primary, [FromKey("Soy")] IMilk secondary)
        => new(primary, secondary);
}
```

The `[FromKey]` on each factory parameter selects the keyed registration, whether a string key or a typed `MilkKind.Oat`, and `Blend` never mentions Awaiten. A factory parameter can also be a relationship such as `[FromKey("Oat")] Func<IMilk>` or `[FromKey("Oat")] Lazy<IMilk>` to pull a keyed implementation on demand, and it takes an [`[Arg]`](../06-resolution/06-runtime-arguments.md) the same way, for a value supplied at resolve time.

To pick a keyed registration imperatively, outside any constructor, pass the key to `Resolve`. See [keyed resolution](../06-resolution/01-resolving-services.md#keyed-resolution). That reads the container directly, so it puts no attribute on a domain type. A keyed resolve serves plain services only: a relationship such as `Func<IMilk>`, a collection, or the factory of a service with runtime arguments comes under a key through `[FromKey]` injection alone.

## Select directly with `[FromKey]`

A consumer can also name the key itself, with `[FromKey]` on the parameter. It is the most direct form and composes through `Func<T>` and `Lazy<T>`, but it names the key inside the domain type, which then references Awaiten. It is an accepted trade, not a defect, when you would rather annotate the constructor than add a factory.

```csharp
public sealed class LatteRecipe([FromKey("Oat")] IMilk milk);
```

A typed key works the same way, and the enum `MilkKind.Oat` and the string `"Oat"` stay distinct keys:

```csharp
public sealed class LatteRecipe([FromKey(MilkKind.Oat)] IMilk milk);
```

:::tip[When not to reach for this]
Prefer distinct abstractions, or a [composite](./03-composites.md), over one service type selected by a key. A key is not part of the type: two `IMilk` parameters look the same to a reader whatever key selects them, and a key picked at run time from a dictionary or a `Resolve(key)` call is only checked when it runs. Reach for keys (and `WhenInjectedInto`) only when the implementations really are interchangeable variants of one abstraction, like the milk fridge above. Resolving the ambiguity with a key can hide a design that wants to be split into separate types, see [Design principles](../03-design-principles.md#when-power-becomes-a-smell).
:::

## Where the key goes

The container side names a key with `Key =`: on a registration and on an [`[InjectProperty<T>]`](../06-resolution/05-property-injection.md) entry. The injection site selects one with `[FromKey]`: on a constructor or factory parameter, and on an [`[Inject]`](../06-resolution/05-property-injection.md) property, which has no `Key` of its own.

## Decorators and composites on keys

A [decorator](./02-decorators.md#keyed-registrations) wraps only the unkeyed registrations unless it names keys: `Keys = new object?[] { null, "Oat" }` decorates the unkeyed registration and the `"Oat"` one, and `AllKeys = true` decorates every registration of the service. A decorated key is decorated wherever it is reached, through `Resolve`, `[FromKey]` or the keyed dictionary.

A [composite](./03-composites.md#front-a-key) with a `Key` fronts that key: a resolve or `[FromKey]` under the key gets the composite, which fans out over the registrations under that key.

## Want the whole set?

To get every keyed implementation as a map, ask for `IReadOnlyDictionary<string, IMilk>`. See [Keyed dictionaries](../06-resolution/04-keyed-dictionaries.md).

:::note
Two registrations with the same service type and key collide ([AWT117](../09-diagnostics.md#awt117)).
:::

## Where to go next

- [Keyed dictionaries](../06-resolution/04-keyed-dictionaries.md) for the full key-to-instance map.
- [Collections](../06-resolution/03-collections.md) for all unkeyed implementations at once.
- [Context-aware factories](../06-resolution/07-context-aware-factories.md) to build a service based on who asked for it.
