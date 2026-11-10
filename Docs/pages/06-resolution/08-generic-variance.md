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
- Keyed registrations are never variance candidates.

Variance applies to single resolution, to collections (exact matches lead, variance matches are unioned in), to awaited collections, and to `Func<T>` relationships, for constructor parameters and [injected properties](./05-property-injection.md) alike. `Lazy<T>` and `Task<T>` are invariant, so a variance match does not satisfy them.

## Where to go next

- [Open generics](../05-registration/05-open-generics.md) for generic implementations closed on demand.
- [Collections](./03-collections.md) for resolving every match.
