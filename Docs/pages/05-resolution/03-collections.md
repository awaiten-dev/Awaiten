---
description: Resolve every implementation of a service as a collection, synchronously or as an async collection.
---

# Collections

When a service has several implementations, ask for a collection to get them all. Every syrup on the bar is an `ISyrup`. A consumer that wants the whole set asks for a collection of them.

## Resolve every implementation

Register the implementations, then depend on a collection shape.

```csharp
[Container]
[Singleton<VanillaSyrup, ISyrup>]
[Singleton<CaramelSyrup, ISyrup>]
[Singleton<HazelnutSyrup, ISyrup>]
public static partial class CoffeeShop;

public sealed class SyrupBar(IEnumerable<ISyrup> syrups);
```

## Supported shapes

Any of these constructor parameter types works, and each is resolvable by type as well.

```csharp no-compile
IEnumerable<ISyrup>
ISyrup[]
IReadOnlyList<ISyrup>
IReadOnlyCollection<ISyrup>
IList<ISyrup>
ICollection<ISyrup>
```

Members come back in precedence order: registrations without `Fallback` first, then [overridable defaults](../04-registration/07-modules.md#overridable-defaults), then the closings of [open generic](../04-registration/05-open-generics.md) registrations (those without `Fallback` first), then [scan](../04-registration/06-scanning.md) matches, each group in declaration order, with the container's own registrations before those of the imported modules. Each keeps its own lifetime. A service type with no registrations resolves to an empty collection, not an error.

A collection holds the services themselves, not [relationships](./02-relationships.md) over them. A collection of relationships, such as `IEnumerable<Lazy<ISyrup>>` or `Func<ISyrup>[]`, is an error ([AWT254](../09-diagnostics.md#awt254)) rather than an empty collection. Depend on `IEnumerable<ISyrup>` for every member, or on a single `Lazy<ISyrup>` to defer the one single resolution picks.

Every registration of the service is a member, also one that loses single resolution to an earlier registration. The only registration left out is an overridable default that a registration of the same service without `Fallback` replaces. Two defaults don't replace each other, so both are members. A single `ISyrup` resolves to one member, the first one. When several modules [contribute to one collection](../04-registration/07-modules.md#contribute-to-a-collection), a dependency on a single `ISyrup` is reported, because only the order of the imports decides which member it gets.

## Async collections

If a member is async-initialized, you have two shapes.

`IAsyncEnumerable<T>` streams the members and awaits each one's initialization as you go.

```csharp
public sealed class Warmer(IAsyncEnumerable<IDrink> drinks);
```

An awaited collection like `Task<IReadOnlyList<T>>` materializes and awaits every async member, but launders the taint so a synchronous consumer can still hold it.

```csharp
Task<IReadOnlyList<IDrink>> drinks = shop.Resolve<Task<IReadOnlyList<IDrink>>>();
```

## Registering your own collection

Register a collection shape explicitly and it wins. Awaiten stops synthesizing the collection for that element type and uses yours. A dependency on a `Task` of the shape you registered, here `Task<IEnumerable<ISyrup>>`, gets a task over your registration, while the other shapes and their `Task` forms are no longer available.

```csharp
[Singleton<SyrupBundle, IEnumerable<ISyrup>>]
```

## Where to go next

- [Keyed dictionaries](./04-keyed-dictionaries.md) to get implementations keyed by name.
- [Composites](../04-registration/03-composites.md) to expose many implementations as one service.
