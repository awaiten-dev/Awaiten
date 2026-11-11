---
description: Fill settable properties after construction with [InjectProperty<T>] on the container, or [Inject] on the property as an escape hatch.
---

import Tabs from '@theme/Tabs';
import TabItem from '@theme/TabItem';

# Property injection

Awaiten fills constructor parameters by default. Property injection is the opt-in alternative for the odd case where a constructor does not fit, like breaking a cycle between two services. It is done through an object initializer, so there is still no reflection.

There are two ways to ask for it. Declaring it on the container with `[InjectProperty<T>]`, or its `typeof` form for open generics, keeps the service a plain class, with no Awaiten reference in your domain code. That is the idiom. Marking the property itself with `[Inject]` is the escape hatch for the one shape the container-side form cannot express.

<Tabs groupId="propertyInjection">

<TabItem value="container" label="On the container" default>

```csharp no-compile
public sealed class Barista
{
    public ITimeSystem? Time { get; set; }
}

[InjectProperty<Barista>(nameof(Barista.Time))]   // on the [Container]
```

</TabItem>

<TabItem value="inject" label="[Inject] on the property">

```csharp no-compile
public sealed class Barista
{
    [Inject] public ITimeSystem? Time { get; set; }   // Barista now references Awaiten
}
```

</TabItem>

</Tabs>

## Declare it on the container with `[InjectProperty<T>]`

Point `[InjectProperty<TImplementation>]` at a settable property by name, on the container. Awaiten fills it after construction, exactly as if the property carried `[Inject]`, but the implementation stays a plain class.

```csharp
public sealed class Barista
{
    public ITimeSystem? Time { get; set; }   // a plain property, no Awaiten attribute
}

[Container]
[Singleton<RealTimeSystem, ITimeSystem>]
[Singleton<Barista>]
[InjectProperty<Barista>(nameof(Barista.Time))]
public static partial class CoffeeShop;
```

Pass `nameof(Barista.Time)` so a rename carries through. Repeat the attribute, once per property. It reads the *implementation* type, so it applies wherever that type is constructed, including a type pulled in by a [`[Scan]`](../05-registration/07-scanning.md) that a per-registration list of names could never reach.

## Open generics with the `typeof` form

Every entry also comes in a `typeof` form. For a closed type the two are the same entry:

```csharp no-compile
[InjectProperty<Barista>(nameof(Barista.Time))]           // generic form
[InjectProperty(typeof(Barista), nameof(Barista.Time))]   // typeof form, same behavior
```

The `typeof` form exists for an unbound generic, which cannot be a type argument: `typeof(OrderBook<>)` fills the property on every closing of `OrderBook<T>` that the container constructs, whether an [open-generic registration](../05-registration/06-open-generics.md) or an explicit closed one produced it.

```csharp
public interface IOrderBook<T>;

public sealed class OrderBook<T> : IOrderBook<T>
{
    public ITimeSystem? Time { get; set; }   // a plain property, no Awaiten attribute
}

public sealed class CashDesk(IOrderBook<Order> orders, IOrderBook<Customer> regulars);

[Container]
[Singleton<RealTimeSystem, ITimeSystem>]
[Singleton(typeof(OrderBook<>), typeof(IOrderBook<>))]
[Transient<CashDesk>]
[InjectProperty(typeof(OrderBook<>), nameof(OrderBook<object>.Time))]   // fills OrderBook<Order> and OrderBook<Customer>
public static partial class CoffeeShop;
```

Each closing is checked on its own, with the property type substituted: a property of type `IValidator<T>` resolves `IValidator<Order>` on `OrderBook<Order>`. `Key`, `Optional` and `Deferred` work as on the generic form. Name the property with any closing, such as `nameof(OrderBook<object>.Time)`. A class that only derives from a closing is not a closing and is left alone. The argument must be a closed type or an unbound generic definition; anything in between, such as `typeof(Outer<>.Inner)`, is [AWT215](../09-diagnostics.md#awt215).

A closed entry wins over an open one: when both name the same property, the closed entry applies on its closing and the open entry on every other closing. Use this to specialize one closing, for example to select a different keyed registration:

```csharp no-compile
[InjectProperty(typeof(OrderBook<>), nameof(OrderBook<object>.Time))]   // every closing
[InjectProperty<OrderBook<Order>>(nameof(OrderBook<Order>.Time), Key = "Utc")]   // OrderBook<Order> differs on purpose
```

The closed entry has to change at least one of `Key`, `Optional` and `Deferred`. One that repeats the open entry's options adds nothing and is flagged as a duplicate ([AWT179](../09-diagnostics.md#awt179)).

## Any dependency shape works

A container-side property resolves the same way a constructor parameter does: relationships, collections, and keyed registrations all work. The one difference is [`[ImportServices]`](../07-hosting/02-external-services.mdx#fall-through-everything-with-importservices): an unregistered property does not fall through to the external provider, it is a missing dependency like any other. Each entry carries its own `Optional`, `Deferred`, and `Key`.

### Select a keyed registration with `Key`

```csharp no-compile
[InjectProperty<Barista>(nameof(Barista.DefaultMilk), Key = "Oat")]
```

`Key` takes a `string`, an `enum` value, or a `typeof(...)`, exactly like a [`[FromKey]`](../05-registration/05-keyed-services.md) on an `[Inject]` property.

### Break a cycle with `Deferred`

Two services that need each other cannot both be built first. Mark one side `Deferred = true`, and Awaiten caches the owner before wiring the property, so the cycle terminates.

```csharp
[InjectProperty<OrderService>(nameof(OrderService.Invoice), Deferred = true)]
```

A deferred property needs an accessible plain `set`, not `init` ([AWT144](../09-diagnostics.md#awt144)). A cycle that cannot terminate, for example one made entirely of transients, is still a build error ([AWT145](../09-diagnostics.md#awt145)).

### Tolerate a missing service with `Optional`

By default a property whose service is unregistered is a missing dependency ([AWT101](../09-diagnostics.md#awt101)). Mark the entry `Optional = true` to leave it at its default instead.

```csharp no-compile
[InjectProperty<Barista>(nameof(Barista.Metrics), Optional = true)]
```

A service an [open generic](../05-registration/06-open-generics.md) registration can provide counts as registered: a property of type `IRepository<Order>` closes `[Singleton(typeof(Repository<>), typeof(IRepository<>))]` exactly like a constructor parameter would, so it is filled even when it is `Optional`.

`Optional` tolerates a missing registration, not a request that no registration could serve. A `Lazy<Owned<T>>` ([AWT121](../09-diagnostics.md#awt121)) or a relationship over an [imported service](../07-hosting/02-external-services.mdx#relationships-and-collections) ([AWT242](../09-diagnostics.md#awt242)) is reported for an optional property too, also when an imported module of another assembly builds the implementation.

### Rules for entries

- A bad name, a read-only member, or a field is [AWT177](../09-diagnostics.md#awt177). For an open-generic target it is checked once, on the generic definition.
- An unreachable setter is [AWT136](../09-diagnostics.md#awt136).
- A duplicate entry for one property is [AWT179](../09-diagnostics.md#awt179).
- A type a `Factory` or `Instance` produces is built whole by its source, with nothing for the container to fill, so an entry for it is [AWT178](../09-diagnostics.md#awt178).
- A type that a module of another assembly builds still gets the entry: the container sets the property on the instance it receives, before the module's hooks and initializers run. There the entry is [AWT178](../09-diagnostics.md#awt178) when the property is init-only, when the entry is `Deferred`, or when the module builds the type from runtime `[Arg]` arguments. The [library module rules](../05-registration/08-modules/02-library-module-rules.md#container-side-property-entries) list every case.
- An entry naming a [decorator](../05-registration/03-decorators.md) type is ignored without a diagnostic, because the decorator chain builds the decorator. Use `[Inject]` on the decorator's property, as described below.

A property without an entry is left alone. Injection stays opt-in, one property at a time, and if a property carries both `[Inject]` and an `[InjectProperty<T>]` entry the `[Inject]` member wins and the redundant entry is flagged ([AWT181](../09-diagnostics.md#awt181)).

## The escape hatch: `[Inject]` on the property

Marking the property itself with `[Inject]` asks for the same injection from the domain side. It fills exactly like a container-side entry, and `Optional`, `Deferred`, and a `[FromKey]` alongside it all work.

`[Inject]` puts an Awaiten reference on your class, so reach for it only for the one shape `[InjectProperty]` cannot express: a property of a **decorator**, which its decorator chain builds rather than an entry filling it. An `[InjectProperty]` entry that names a decorator type has no effect and is not reported, so the property stays unset. For every other type, including an open generic one, prefer the container-side form. Where it applies, `[Inject]` is an accepted trade, not a defect.

```csharp
public sealed class AuditedTerminal(IPaymentTerminal inner) : IPaymentTerminal
{
    // highlight-next-line
    [Inject] public ITimeSystem? Time { get; set; }   // set when the chain builds the decorator

    public Task ChargeAsync(decimal amount) => inner.ChargeAsync(amount);
}

[Container]
[Singleton<RealTimeSystem, ITimeSystem>]
[Singleton<CardTerminal, IPaymentTerminal>]
[Decorate<AuditedTerminal, IPaymentTerminal>]
public static partial class CoffeeShop;
```

<details>
<summary>Internal and private <code>[Inject]</code> properties</summary>

- An `internal` `[Inject]` property, or one with an `internal` setter, is set by a container in its own assembly. A container in another assembly that constructs the type itself reports it instead of leaving it unset: [AWT244](../09-diagnostics.md#awt244) for an `internal` property, [AWT136](../09-diagnostics.md#awt136) for an `internal` setter. Register the type in a [module](../05-registration/08-modules/index.md) of its own assembly instead, see the [library module rules](../05-registration/08-modules/02-library-module-rules.md#injected-properties).
- A `private` `[Inject]` property or an explicit interface implementation can't be assigned by any container, so it's [AWT136](../09-diagnostics.md#awt136) wherever the container lives.

</details>

:::tip[When not to reach for this]
Prefer constructor injection. A constructor parameter states a dependency plainly and cannot be forgotten; a property can be left unset. Reach for property injection only for a genuinely optional dependency with a sensible local default, or to break a cycle two constructors cannot. If you are using it to shorten a long constructor, that constructor is telling you the class does too much, see [Design principles](../03-design-principles.md#when-power-becomes-a-smell).
:::

## Where to go next

- [Relationships](./02-relationships.md) for deferred construction without property injection.
- [Keyed services](../05-registration/05-keyed-services.md) for `[FromKey]` and container-side keyed selection.
- [Resolving services](./01-resolving-services.md) for the resolve surface.
