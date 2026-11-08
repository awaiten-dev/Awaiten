---
description: Register an open generic type once and let Awaiten close it for every type argument the graph uses.
---

# Open generics

A generic service like `IRepository<T>` usually has one generic implementation like `Repository<T>`. You do not want to register it once per entity type. Register the open generic once, and Awaiten closes it for each type your app actually uses.

## Register the open type

Use the `typeof` form with an unbound generic type.

```csharp
[Container]
[Transient(typeof(Repository<>), typeof(IRepository<>))]
public static partial class CoffeeShop;
```

## Resolve a closed type

Awaiten works out which closed forms you need at compile time from what the graph actually asks for: constructor and factory parameters, including those of [decorators](02-decorators.md) and [composites](03-composites.md), [injected properties](../05-resolution/05-property-injection.md) and the parameters of [lifecycle hooks](../06-lifetime/03-lifecycle-hooks.md), also when the closed service sits inside a collection, a keyed dictionary, a `Lazy<T>`, a `Func<T>`, a `Task<T>` or an `Owned<T>`. Each of those type arguments gets its own closed implementation, and each keeps the declared lifetime. You resolve a closing the graph asks for like any other service.

```csharp
IRepository<Order> orders = shop.Resolve<IRepository<Order>>();
```

A closing that nothing in the graph asks for is not generated, so resolving it throws at run time. Awaiten warns about a typed resolve of such a closing on the generated `Root` or `Scope` ([AWT218](../09-diagnostics.md#awt218)). When you resolve a closing directly, for example from a test, register it explicitly next to the open registration:

```csharp
[Container]
[Transient(typeof(Repository<>), typeof(IRepository<>))]
[Transient<Repository<Customer>, IRepository<Customer>>]   // resolved directly, never injected
public static partial class CoffeeShop;
```

## Self-registration

You can register an open generic under itself, without a separate service type.

```csharp
[Transient(typeof(Handler<>))]
// resolve: shop.Resolve<Handler<OrderPlaced>>()
```

## Collections and keys

Open registrations join collections. A request for `IEnumerable<IHandler<OrderPlaced>>` unions every open registration closed at `OrderPlaced`, alongside any explicit closed registrations. A `Key` on the open registration flows to every closed implementation, so you select with `[FromKey]` as usual.

```csharp
[Transient(typeof(FastRepository<>), typeof(IRepository<>), Key = "fast")]
```

## Split the closings by constraints

Several open registrations of one service can share its closings by the constraints of their implementations. Awaiten builds each closing only from the registrations that accept its type arguments and skips the others for that closing:

```csharp
public interface IPastry;
public sealed class Croissant : IPastry;

public interface IRecipe<T>;
public sealed class BrewRecipe<T> : IRecipe<T> where T : IDrink;
public sealed class BakeRecipe<T> : IRecipe<T> where T : IPastry;

public sealed class Kitchen(IRecipe<Espresso> espresso, IRecipe<Croissant> croissant);

[Container]
[Transient(typeof(BrewRecipe<>), typeof(IRecipe<>))]
[Transient(typeof(BakeRecipe<>), typeof(IRecipe<>))]
[Transient<Kitchen>]
public static partial class CoffeeShop;
```

`IRecipe<Espresso>` is a `BrewRecipe<Espresso>` and `IRecipe<Croissant>` a `BakeRecipe<Croissant>`. The constraints of a generic `Factory` select its closings the same way.

- **Several accept a closing.** The rules for several registrations apply as usual: the first one wins single resolution, `IEnumerable<IRecipe<T>>` holds every registration that accepts `T`, and two modules that both build a closing are reported for it ([AWT155](../09-diagnostics.md#awt155)).
- **None accepts a closing.** A closing the graph asks for that neither an open nor a closed registration builds is an error that lists the candidates ([AWT126](../09-diagnostics.md#awt126)).
- **Options.** `Fallback` and `WhenInjectedInto` apply among the registrations that accept the closing, so an overridable default or an unconditional registration serves the closings a stronger or contextual one can't build.
- **Keys.** A skipped registration is not reported when a registration under another key, or a contextual one, builds the closing. A request that only the skipped registration would have served is then a missing registration ([AWT101](../09-diagnostics.md#awt101)).
- **Decorators and composites** wrap every registration of a closing. One whose constraints reject a closing leaves it as it is, with a warning ([AWT171](../09-diagnostics.md#awt171)).
- **Extending containers.** An open registration of the extending container replaces the inherited one for every closing, also for those it can't build.

## Decorate or compose the closings

Decorators and composites take the same `typeof` form, so one behavior wraps every closing of an open generic service. This is the pipeline-behavior pattern:

```csharp
[Transient(typeof(Handler<>), typeof(IHandler<>))]
[Decorate(typeof(LoggingBehavior<>), typeof(IHandler<>))]
[Composite(typeof(CompositeHandler<>), typeof(IHandler<>))]
```

See [Decorators](./02-decorators.md#decorate-every-closing-of-an-open-generic) and [Composites](./03-composites.md#compose-every-closing-of-an-open-generic).

:::note
The implementation and service must have the same arity ([AWT125](../09-diagnostics.md#awt125)), and a closing the graph asks for must satisfy the constraints of at least one registration ([AWT126](../09-diagnostics.md#awt126)).
:::

## Registration options

The `typeof` form takes the same options as the generic form. On an open generic each option applies to every closing.

| Option | Open generic | Closed `typeof` |
|---|---|---|
| `Key` | yes, every closing is keyed | yes |
| `Fallback` | yes, per closing | yes |
| `WhenInjectedInto` | yes | yes |
| `SuppressDisposal` | yes | yes |
| `OnActivated` / `OnRelease` | yes, the hook may be generic | yes |
| `Factory` | a generic method, closed per closing | yes |
| `Instance` (`[Singleton]` only) | no ([AWT207](../09-diagnostics.md#awt207)) | yes |
| `Eager` (`[Singleton]` only) | no ([AWT207](../09-diagnostics.md#awt207)) | yes |

A closed `typeof` registration, such as `[Singleton(typeof(Clock), typeof(IClock))]`, is the same registration as `[Singleton<Clock, IClock>]`. The generic form checks at compile time that the implementation is a class that implements the service; the `typeof` form gets the same check from Awaiten ([AWT206](../09-diagnostics.md#awt206)).

`Instance` and `Eager` describe one concrete instance. An open generic only becomes concrete per closing, and which closings exist depends on what the rest of the graph asks for, so both need a closed type. Register the closings you want eager, or pre-built, explicitly.

### Generic factories

`Factory` on an open generic names a generic method. Awaiten closes it for each closing, just like the implementation:

```csharp
[Container]
[Scoped(typeof(IRepository<>), Factory = nameof(CreateRepository))]
public static partial class CoffeeShop
{
    private static IRepository<T> CreateRepository<T>(DbConnection connection)
        => new Repository<T>(connection);
}
```

A request for `IRepository<Order>` calls `CreateRepository<Order>(connection)`. The rules:

- **Type parameters.** The method has exactly the service's type parameters, mapped in declaration order, like open decorators and composites. A method with a different number is an error ([AWT208](../09-diagnostics.md#awt208)).
- **Constraints.** A closing that violates the method's constraints is skipped, like a closing that violates the implementation's own. When no other registration builds it, that's an error ([AWT209](../09-diagnostics.md#awt209)).
- **Return type.** The method returns the closed service, or `Task<T>`/`ValueTask<T>` of it. An async factory is awaited and makes the closing async, like a non-generic async factory.
- **Parameters.** They follow the rules of a non-generic factory: graph dependencies (including other open generics closed the same way), `CancellationToken`, `[FromKey]`, `[RequestingType]`, and `[Arg]` on a transient reached through `Func<…, T>`.
- **Overloads.** An overloaded factory is ambiguous ([AWT112](../09-diagnostics.md#awt112)), as for a non-generic one.

### Lifecycle hooks

`OnActivated` and `OnRelease` run for every closing. The hook can be generic over the service's type arguments, which it receives like a hook on an [open-generic scan](./06-scanning.md#generic-hooks-on-an-open-marker): Awaiten binds it from the closed service, so `IConnection<Order>` calls `Opened<Order>(…)`. A non-generic hook, with its first parameter typed as `object` or a shared base, works for every closing too. A hook of a module's open registration that another assembly can't call runs through a [generic wrapper](./07-modules.md#open-generics) the module generates.

```csharp
[Container]
[Scoped(typeof(Connection<>), typeof(IConnection<>), OnActivated = nameof(Opened))]
public static partial class CoffeeShop
{
    private static void Opened<T>(IConnection<T> connection, ILogger logger)
        => logger.Log($"Opened a connection for {typeof(T).Name}");
}
```

### Overridable defaults

`Fallback` makes an open generic an overridable default, typically in a [module](./07-modules.md#overridable-defaults). It applies per closing: a closed registration without `Fallback` replaces the default for its own closing only, and a strong open registration of the same service replaces it for every closing it can build. A closed default replaces nothing, so the open default's closing stays a member of that closing's collections, behind the closed one.

```csharp
[Container]
[Singleton(typeof(MemoryCache<>), typeof(ICache<>), Fallback = Fallback.Warn)]
[Singleton<OrderCache, ICache<Order>>]   // ICache<Order> is OrderCache, every other closing MemoryCache<T>
public static partial class CoffeeShop;
```

## Where to go next

- [Collections](../05-resolution/03-collections.md) for resolving every implementation.
- [Generic variance](../05-resolution/08-generic-variance.md) for covariant and contravariant matching.
