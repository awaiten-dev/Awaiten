---
description: Pass values known only at resolve time to a service through [Arg] parameters and a matching Func.
---

# Runtime arguments

Some values are only known when you resolve, not when you register. The size of a cup depends on what the customer ordered. A `[Arg]` parameter is filled from your call instead of the graph, and you supply it through a `Func`. Put the `[Arg]` on a container factory method to keep the produced type a plain class, or on the constructor directly when you would rather not add a factory.

## Keep the argument off the domain type with a factory

Put `[Arg]` on a container factory-method parameter. The produced type keeps a plain constructor; the runtime argument is forwarded to the factory, while the rest of the parameters still resolve from the graph, and you supply the argument through the same `Func`.

```csharp
public sealed class Cup(EspressoMachine machine, string size)   // a plain class, no attributes
{
    public string Size { get; } = size;
}

[Container]
[Singleton<EspressoMachine>]
[Transient<Cup>(Factory = nameof(MakeCup))]
public static partial class CoffeeShop
{
    private static Cup MakeCup(EspressoMachine machine, [Arg] string size) => new(machine, size);
}
```

`shop.Resolve<Func<string, Cup>>()("Large")` builds a large cup, and `Cup` never mentions Awaiten.

## Mark a parameter with `[Arg]`

You can also mark the constructor parameter directly. The rest of the constructor still resolves from the graph; only the `[Arg]` parameters come from you.

```csharp
public sealed class Cup
{
    public Cup(EspressoMachine machine, [Arg] string size)
    {
        Size = size;
    }

    public string Size { get; }
}

[Container]
[Singleton<EspressoMachine>]
[Transient<Cup>]
public static partial class CoffeeShop;
```

`[Arg]` on a constructor parameter couples the domain type to Awaiten, which the factory form above avoids. Reach for the direct form when you would rather annotate the constructor than add a factory; it is an accepted trade, not a defect.

## Supply the value through a Func

A service with `[Arg]` parameters is resolved through a matching `Func<TArg..., T>`. The arguments are matched positionally.

```csharp
Func<string, Cup> makeCup = shop.Resolve<Func<string, Cup>>();
Cup large = makeCup("Large");
```

A consumer usually injects that `Func`:

```csharp
public sealed class Barista(Func<string, Cup> cups)
{
    public Cup Serve(string size) => cups(size);
}
```

## Several arguments

List every `[Arg]` and pass a `Func` with the matching shape. The `Func` arguments follow the order of the `[Arg]` parameters of the constructor or factory method.

```csharp
public sealed class Receipt(Store store, [Arg] string customer, [Arg] int number);

// Resolve<Func<string, int, Receipt>>()
```

## Async initialization

When the service is initialized asynchronously, ask for `Func<TArg..., Task<T>>` instead. Each call builds the service from your arguments, and the task completes once it is initialized. The async factory works for a service without async initialization too, and then hands back a completed task.

```csharp
Func<string, Task<Cup>> brewCup = shop.Resolve<Func<string, Task<Cup>>>();
Cup medium = await brewCup("Medium");
```

## Rules

- A service with `[Arg]` parameters must be transient ([AWT114](../09-diagnostics.md#awt114)), because each call builds a fresh instance from your arguments.
- It is reachable only through `Func<TArg..., T>`, `Func<TArg..., Owned<T>>`, `Func<TArg..., Task<T>>` or `Func<TArg..., Task<Owned<T>>>`. A plain, `Lazy<T>`, `Owned<T>` or `Task<T>` dependency on it is [AWT115](../09-diagnostics.md#awt115).
- The `Func` arguments must match the `[Arg]` parameters in type and order; a `Func` that supplies other arguments, or none, is [AWT113](../09-diagnostics.md#awt113).
- A synchronous `Func<TArg..., T>` over an async-initialized service is [AWT119](../09-diagnostics.md#awt119); take the `Task` form instead.
- The argument wins even when its type is itself registered. A `[Arg] string` always comes from your call, never from the graph.

## Where to go next

- [Relationships](./02-relationships.md) for `Func<T>` without arguments.
- [Owned](../04-core-concepts/06-owned.md) for `Func<TArg, Owned<T>>` when the built service is disposable.
