---
description: Resolve covariant and contravariant generic interfaces from registrations of a base or derived type argument.
---

# Generic variance

C# generic interfaces can be covariant (`out T`) or contravariant (`in T`). Awaiten honors that when it resolves. A handler registered for a base event type can satisfy a request for a derived one, the same way the compiler would allow the assignment.

## Contravariant handlers

A handler declared with `in T` accepts more derived types. Register one for `DomainEvent` and it satisfies a request for `IHandler<OrderPlaced>`.

```csharp
public interface IHandler<in T>;
public sealed class AuditHandler : IHandler<DomainEvent>;

[Container]
[Transient<AuditHandler, IHandler<DomainEvent>>]
[Transient<OrderConsumer>]      // needs IHandler<OrderPlaced>
public static partial class CoffeeShop;
```

```csharp
IHandler<OrderPlaced> handler = shop.Resolve<IHandler<OrderPlaced>>();   // the AuditHandler
```

## Covariant producers

A producer declared with `out T` hands out more derived types. Register one for `OrderPlaced` and it satisfies a request for `IFeed<DomainEvent>`.

```csharp
public interface IFeed<out T> where T : DomainEvent;
public sealed class OrderPlacedFeed : IFeed<OrderPlaced>;

[Container]
[Transient<OrderPlacedFeed, IFeed<OrderPlaced>>]
public static partial class CoffeeShop;
```

```csharp
IFeed<DomainEvent> feed = shop.Resolve<IFeed<DomainEvent>>();   // the OrderPlacedFeed
```

## The rules

- An exact registration always wins over a variance match.
- When several registrations match, the nearest one wins: the one the services of all the others convert to. For `IHandler<OrderPlaced>`, a registration of `IHandler<DomainEvent>` is nearer than one of `IHandler<object>`. Registrations that are equally near keep declaration order.
- Invariant interfaces never match a different closing.
- Keyed registrations are never variance candidates, and a `[FromKey]` dependency never takes a variance match.
- A direct dependency, a `Func<T>` and a `Func<TArg…, T>` take a variance match.
- Every unkeyed collection shape takes variance matches too, including `IAsyncEnumerable<T>` and the awaited collections: the exact matches lead, and the variance matches follow.
- The other relationships, `Lazy<T>`, `Task<T>`, `Owned<T>`, `Func<..., Owned<T>>` and the `Task` forms such as `Func<Task<T>>`, take only an exact registration. Over a closing that only a variance match provides, they are a missing dependency ([AWT101](../09-diagnostics.md#awt101)).
- All of this holds for constructor parameters and [injected properties](./05-property-injection.md) alike.

## Where to go next

- [Open generics](../05-registration/06-open-generics.md) for generic implementations closed on demand.
- [Collections](./03-collections.md) for resolving every match.
