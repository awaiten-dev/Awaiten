---
description: Build a service with a factory method or register an object you already have as an instance.
---

# Factories and instances

Most services are built by their constructor, and Awaiten calls it for you. Sometimes that is not enough. You need a bit of logic to build the service, or you already have the object in hand. That is what factories and instances are for.

## Factory

Point a registration at a method with `Factory = nameof(...)`. The method builds the service. Its parameters resolve from the graph, just like a constructor. The result is cached according to the lifetime.

```csharp
[Container]
[Singleton<Settings>]
// highlight-next-line
[Singleton<IBrewer>(Factory = nameof(MakeBrewer))]
public static partial class CoffeeShop
{
    private static IBrewer MakeBrewer(Settings settings)
        => settings.PreferFilter ? new FilterBrewer() : new EspressoBrewer();
}
```

The method is `static`, like the container class that declares it. A factory that returns `Task<T>` makes the service async. See [Async initialization](../04-core-concepts/03-async-initialization.md).

## Instance

Hand Awaiten an object you already built with `Instance = nameof(...)`. Awaiten uses it as-is. It never constructs it and never disposes it, because the container does not own it.

```csharp
[Container]
// highlight-next-line
[Singleton<MenuBoard>(Instance = nameof(Menu))]
public static partial class CoffeeShop
{
    private static MenuBoard Menu { get; } = MenuBoard.LoadFromDisk();
}
```

Use this for configuration objects or anything with a lifetime you manage yourself. For the same reason, no [async initializer convention](../04-core-concepts/03-async-initialization.md#which-conventions-apply) runs on it: initialize it before you hand it over.

## Several producers of one service

A `Factory` or `Instance` registration that names only the service, like `[Singleton<IBrewer>(Factory = nameof(MakeBrewer))]`, is identified by its member:

- **Different members** of one service are two implementations, just like two classes. The first registration, or the container's over a module's, wins single resolution, and both are members of the collection.
- **The same member** named by several registrations is one implementation, with one shared instance.
- **A named implementation.** When a registration names the type as its implementation, as in `[Singleton<Grinder>]`, every registration of that type is one implementation and must produce it the same way ([AWT111](../09-diagnostics.md#awt111)).
- **Open generics.** An open generic `Factory` that names only its service, like `[Transient(typeof(IRepository<>), Factory = nameof(CreateRepository))]`, is identified by its member for every closing (see [Generic factories](./06-open-generics.md#generic-factories)). Two of them with different members both join `IEnumerable<IRepository<Order>>`, and the first, or the container's over a module's, wins `IRepository<Order>`.
- **One implementation, one set of directives.** Registrations that close into one implementation, such as the same factory or the same implementation type registered twice, share one instance and must agree on its lifetime and lifecycle directives ([AWT107](../09-diagnostics.md#awt107), [AWT166](../09-diagnostics.md#awt166)).

## Disposal follows the real type

A factory can return an interface that hides a disposable concrete type. Awaiten still disposes it, because it tracks the actual object it got back, and it disposes it exactly once with its owner.

An `Instance` member is the opposite. You own it, so Awaiten leaves its disposal to you.

To keep a factory-built service but opt out of that disposal, set `SuppressDisposal = true`. Awaiten builds it and never disposes it, leaving teardown to you or to an `OnRelease` hook, which is how you [pool a rented object](../04-core-concepts/05-disposal.md#suppressing-disposal).

## Rules

- A `Factory` must name an accessible method that returns the registered service ([AWT108](../09-diagnostics.md#awt108)), and only one such method of that name may exist ([AWT112](../09-diagnostics.md#awt112)).
- An `Instance` must name an accessible field or property of the registered service type ([AWT109](../09-diagnostics.md#awt109)).
- A `Factory` cannot name a generic method, whose type parameters only the closings of an [open generic](./06-open-generics.md#generic-factories) registration supply ([AWT108](../09-diagnostics.md#awt108)).
- A registration cannot set both `Factory` and `Instance` ([AWT110](../09-diagnostics.md#awt110)).
- Registrations of one implementation must produce it the same way ([AWT111](../09-diagnostics.md#awt111)) and agree on its lifetime and lifecycle directives ([AWT107](../09-diagnostics.md#awt107), [AWT166](../09-diagnostics.md#awt166)).
- A synchronous factory that returns an async-initialized type behind a plainer return type hides its initialization, which then never runs ([AWT106](../09-diagnostics.md#awt106)). Return the concrete type, or make the factory `async`.

## Where to go next

- [Context-aware factories](../06-resolution/07-context-aware-factories.md) to build a service based on who asked for it.
- [Lifecycle hooks](./02-lifecycle-hooks.md) to run code when an instance is activated or released.
