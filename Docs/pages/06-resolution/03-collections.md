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

Any of these constructor parameter types works. Each is resolvable by type as well when the collection is unkeyed and no member is [async-tainted](../12-reference/03-glossary.md#async-tainted). With an async-tainted member, resolve one of the [async collections](#async-collections) instead.

```csharp no-compile
IEnumerable<ISyrup>
ISyrup[]
IReadOnlyList<ISyrup>
IReadOnlyCollection<ISyrup>
IList<ISyrup>
ICollection<ISyrup>
```

Members come back in precedence order:

1. Registrations without `Fallback`.
2. [Overridable defaults](../05-registration/08-modules/index.md#overridable-defaults).
3. Closings of [open generic](../05-registration/06-open-generics.md) registrations, those without `Fallback` first.
4. [Scan](../05-registration/07-scanning.md) matches.

Within each group they keep declaration order, with the container's own registrations before those of the imported modules. Each member keeps its own lifetime. A service type with no registrations resolves to an empty collection, not an error.

With [`[FromKey]`](../05-registration/05-keyed-services.md#select-directly-with-fromkey) on the parameter or property, such as `[FromKey("Oat")] IEnumerable<IMilk>`, the collection holds the registrations under that key instead. A collection under a key is served only to injection: a keyed `Resolve<IEnumerable<IMilk>>("Oat")` throws. When the call is on the generated `Root` or `Scope` and the key is a constant, Awaiten warns about it at compile time ([AWT218](../09-diagnostics.md#awt218)).

A collection holds the services themselves and a relationship wraps a single service, so these are errors:

- A collection of relationships, such as `IEnumerable<Lazy<ISyrup>>` or `Func<ISyrup>[]` ([AWT254](../09-diagnostics.md#awt254)), unless the relationship type itself is registered as a service. Then the collection holds that registration.
- A relationship over a collection, such as `Func<IEnumerable<ISyrup>>` ([AWT256](../09-diagnostics.md#awt256)), unless the collection type itself is [registered](#registering-your-own-collection).

See [Relationships wrap a single service](./02-relationships.md#relationships-wrap-a-single-service).

Every registration of the service is a member, also one that loses single resolution to an earlier registration. The only registration left out is an overridable default that an explicit registration of the same service without `Fallback` replaces. A scan match never replaces a default, and the closing of an open generic registration does not replace a closed default; both stay members behind it. Two defaults don't replace each other, so both are members. A single `ISyrup` resolves to one member, the first one. When several modules [contribute to one collection](../05-registration/08-modules/index.md#contribute-to-a-collection), a dependency on a single `ISyrup` is reported, because only the order of the imports decides which member it gets.

## Async collections

A synchronous collection shape builds every member while the consumer is built, so it cannot hold an async-tainted member ([AWT122](../09-diagnostics.md#awt122)). If a member is async-tainted, you have two shapes.

`IAsyncEnumerable<T>` streams the members and awaits each one's initialization as you go. It does not hide the async members from the consumer: when a member is async-tainted, a consumer that takes one is async-tainted too and resolves through `ResolveAsync`.

```csharp
public sealed class Warmer(IAsyncEnumerable<IDrink> drinks);
```

An awaited collection like `Task<IReadOnlyList<T>>` materializes and awaits every async member behind the task, so the consumer itself stays synchronously resolvable.

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
- [Composites](../05-registration/04-composites.md) to expose many implementations as one service.
