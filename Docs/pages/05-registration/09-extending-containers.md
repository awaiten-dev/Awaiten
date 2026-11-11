---
description: Reuse a production container in tests and replace only the services a test needs to control.
---

# Extending containers

A test that exercises the real composition usually wants all of it except one or two volatile services: the payment terminal that would charge a card, the time system that would make the result depend on the time of day. Copying the production container into the test project would work until the two drift apart. `Extends` lets one container start from another container's composition and replace only what differs.

## Extend a production container

Set `Extends` on the `[Container]` of the test project and register the doubles on it.

```csharp
[Container]
[Singleton<CardTerminal, IPaymentTerminal>]
[Singleton<RealTimeSystem, ITimeSystem>]
[Transient<Checkout>]
public static partial class CoffeeShop;

public sealed class Checkout(IPaymentTerminal terminal)
{
    public Task PayAsync(decimal price) => terminal.ChargeAsync(price);
}

public sealed class FakeTerminal : IPaymentTerminal
{
    public decimal Charged { get; private set; }

    public Task ChargeAsync(decimal amount)
    {
        Charged += amount;
        return Task.CompletedTask;
    }
}

[Container(Extends = typeof(CoffeeShop))]
// highlight-next-line
[Singleton<FakeTerminal, IPaymentTerminal>]
[Singleton<FakeTerminal>]
public static partial class TestCoffeeShop;
```

`TestCoffeeShop` compiles as if every declaration of `CoffeeShop` were written on it: its registrations, `[Import]`s, `[Scan]`s, decorators, composites, `[ImportService]` declarations, `[InjectProperty]` entries, lifecycle hooks, [async initializer conventions](../04-core-concepts/03-async-initialization.md#which-conventions-apply), `Eager` singletons and options. Only the `FakeTerminal` differs. Registered as itself too, it is one instance that a test can inspect. The generated `TestCoffeeShop.Root` is a container of its own: nothing at run time links it to `CoffeeShop.Root`, and resolving from it is as fast as from any other container.

## Use it in a test

```csharp
[Test]
public async Task Checkout_ChargesThePrice()
{
    await using TestCoffeeShop.Root shop = new();

    await shop.Resolve<Checkout>().PayAsync(3.50m);

    await That(shop.Resolve<FakeTerminal>().Charged).IsEqualTo(3.50m);
}
```

The `Checkout` comes from the production composition, and its `IPaymentTerminal` is the `FakeTerminal`, so the test never charges a card.

A double can also stay private to the test: nest it in the extending container, or nest the container beside it in the test class. The generated resolvers hand it out as its service, so nothing outside the test sees the type.

```csharp
[Container(Extends = typeof(CoffeeShop))]
[Singleton<FakeTerminal, IPaymentTerminal>]
public static partial class TestCoffeeShop
{
    private sealed class FakeTerminal : IPaymentTerminal;
}
```

## A replacement replaces in full

A registration written directly on the extending container replaces every inherited registration of the same service and key, before anything else is decided. The replaced registration is gone: it is never constructed, it is missing from `IEnumerable<T>` and from keyed collections, and its `Eager` construction and lifecycle hooks go with it. Because it is gone, it does not collide with the replacement either: a replacement with another lifetime or another `Factory` is not a conflict.

- **Keys.** A keyed registration replaces only the inherited registration with the same key. A `WhenInjectedInto` registration has a key of its own per consumer, so a plain registration does not replace it, and a `WhenInjectedInto` registration replaces only the one for the same consumer.
- **Collections.** A registration of `T` replaces every inherited `T`. The extending container cannot add a member to an inherited collection without replacing the others; register every member it should hold.
- **Open generics.** A closed registration replaces its closing of an inherited open generic, and the other closings still come from the base. An open registration replaces an inherited open registration of the same generic.
- **Scans.** A registration of a type the base's `[Scan]` matches owns the type's instance, which the scan's match shares, so the scan's lifecycle hooks do not run on it. Unlike a scan of the container itself ([AWT166](../09-diagnostics.md#awt166)), this is no error: the extending container cannot change the base's scan.
- **Fallback.** A registration with `Fallback` is an [overridable default](./08-modules/index.md#overridable-defaults), as in a module, so it replaces nothing. An inherited registration without `Fallback` replaces it, and next to an inherited default it is a further member of the service's collections, which wins single resolution because the extending container's own registrations come first.
- **Decorators.** The inherited decorators wrap the replacement, also one the host provides through [`[ImportService<T>]`](#per-test-doubles-from-the-host), except an inherited decorator with `AllKeys`, which cannot wrap an import ([AWT123](../09-diagnostics.md#awt123)). A `[Decorate]` on the extending container wraps the inherited decorators, whatever their `Order`, so a test wraps production. An inherited decorator cannot be removed, but a test can [switch it off](#switch-an-inherited-decorator-off).
- **Shared instances.** When the base registers one implementation under several services, replacing one of them leaves the others on the production implementation, which then no longer shares an instance with the replacement.

```csharp
[Container]
[Singleton<PrinterChannel, IReceiptChannel>]
[Singleton<EmailChannel, IReceiptChannel>]
public static partial class Shop;

[Container(Extends = typeof(Shop))]
[Singleton<SmsChannel, IReceiptChannel>]   // IEnumerable<IReceiptChannel> holds only SmsChannel
public static partial class TestShop;
```

The whole graph is validated again. A replacement that breaks a dependency is reported like any other mistake: replacing a singleton's dependency with a scoped one is a [captive dependency](../09-diagnostics.md#awt105). A problem that the base already has is reported only on the base. An error of a base in another assembly is the exception: that assembly built although its graph is broken, for example because the error was suppressed there or the Awaiten version that built it does not report it, so the extending container reports the error too, usually at its `Extends` argument.

## Switch an inherited decorator off

An inherited decorator cannot be removed, but it can be [conditional](./03-decorators.md#apply-a-decorator-conditionally). When its `When` predicate takes a service, such as a settings singleton, the extending container can replace that service, for example with an `Instance`, and so decide whether the decorator applies. A test that must not write an audit trail, say one that goes to a remote store, turns the audit decorator off this way.

```csharp
public sealed class AuditSettings
{
    public bool Enabled { get; init; } = true;
}

public sealed class AuditedTerminal(IPaymentTerminal inner) : IPaymentTerminal;

[Container]
[Singleton<AuditSettings>]
[Singleton<CardTerminal, IPaymentTerminal>]
[Decorate<AuditedTerminal, IPaymentTerminal>(When = nameof(IsAudited))]
public static partial class CoffeeBar
{
    internal static bool IsAudited(AuditSettings settings) => settings.Enabled;
}

[Container(Extends = typeof(CoffeeBar))]
// highlight-next-line
[Singleton<AuditSettings>(Instance = nameof(NoAudit))]
public static partial class TestCoffeeBar
{
    private static readonly AuditSettings NoAudit = new() { Enabled = false };
}

[Test]
public async Task TestCoffeeBar_SkipsTheAudit()
{
    await using TestCoffeeBar.Root bar = new();

    await That(bar.Resolve<IPaymentTerminal>()).IsExactly<CardTerminal>();
}
```

The predicate runs in the extending container. When the test lives in another assembly, the base generates a public wrapper for the internal predicate, and the extending container calls it. A private predicate is an error in any assembly ([AWT247](../09-diagnostics.md#awt247)). An internal one whose parameter types are not all public gets no wrapper, so it is an error in another assembly too, unless the base's assembly grants the test assembly `InternalsVisibleTo`.

## Per-test doubles from the host

To hand each test its own double, declare the service `[ImportService<T>]` on the extending container. That replaces the inherited registration too, and the test supplies the instance through the external resolver. The inherited decorators wrap that instance, and their chain is [transient](./03-decorators.md#decorate-an-imported-service). In the other direction, a registration on the extending container replaces an inherited `[ImportService<T>]`, so a test can register what production draws from its host.

```csharp
using System.Diagnostics.CodeAnalysis;

await using HostedTestCoffeeShop.Root shop = new(new TerminalResolver(new FakeTerminal()));

[Container(Extends = typeof(CoffeeShop))]
// highlight-next-line
[ImportService<IPaymentTerminal>]
public static partial class HostedTestCoffeeShop;

public sealed class TerminalResolver(IPaymentTerminal terminal) : IExternalResolver
{
    public bool TryResolve(Type serviceType, object? key, [NotNullWhen(true)] out object? instance)
    {
        instance = serviceType == typeof(IPaymentTerminal) ? terminal : null;
        return instance is not null;
    }
}
```

See [external services](../07-hosting/02-external-services.mdx) for the resolver and for the MS.DI bridge, which supplies it automatically.

## Extends compared with Import and Fallback

`[Import]` and `Extends` both pull in registrations, but they settle a collision differently:

| | `[Import]` of a `[Module]` | `Extends` of a `[Container]` |
|---|---|---|
| A registration of the container itself | wins single resolution; a strong module registration stays in `IEnumerable<T>` | replaces the inherited registration, collections included |
| Who decides that a registration can be replaced | the module author, per registration, with `Fallback` | the extending container, for any inherited registration |
| What else is inherited | the module's registrations, decorators, composites and imported services | the whole composition, including scans, imports, `[InjectProperty]` and options |
| How many | several modules | one base |

A module `Fallback` registration is dropped in full when the container replaces it, which is the behavior `Extends` gives every inherited registration. The difference is who chooses: `Fallback` makes a registration overridable for every importer, while `Extends` lets one extending container replace what it needs and keeps the production container strict.

## Options

The extending container validates its graph with the options of its base: `AsyncSafety` and `LifetimeSafety` are inherited. Setting either on a container with `Extends` is an error ([AWT225](../09-diagnostics.md#awt225)), so a test graph is checked exactly like the production graph it reuses.

## Accessibility

The generated extending container calls the inherited factories, instance members, lifecycle hooks and constructors from its own code, so it must be able to see them. A private member of the base, or an internal one in another assembly, is reachable from the base's own generated code but not from the extending container. Make such a member public, or internal and grant the test assembly access:

```csharp no-compile
[assembly: InternalsVisibleTo("CoffeeShop.Tests")]
```

- Anything inherited that the extending container cannot reach or build is an error on its `Extends` argument ([AWT226](../09-diagnostics.md#awt226)). That includes an internal match of the scan of a base in another assembly, so the test graph never silently lacks a service the production graph has.
- A match of the base's scan that is private to the base, or that has an `[Inject]` property the extending container doesn't see, is skipped with a warning ([AWT193](../09-diagnostics.md#awt193)) when the extending container can't build it through a factory the base generates.
- An implementation private to the base is built through a factory the base generates, as described below.
- A type that a module the base imports registers is built through the module's generated factory, wherever the extending container lives (see [library module rules](./08-modules/02-library-module-rules.md#internal-implementations)).

<details>
<summary>What an extending container can reach, by where it lives</summary>

| Inherited from the base | Extending container in the base's own assembly | In an assembly the base's assembly grants `InternalsVisibleTo` | In any other assembly |
|---|---|---|---|
| A public member | calls it | calls it | calls it |
| An `internal` member | calls it | calls it | can't reach it |
| A `private` `Factory` or `Instance` member or lifecycle hook | can't reach it: make the member internal there | calls the `internal` wrapper the base generates | can't reach it |
| An `internal` [async initializer convention](../04-core-concepts/03-async-initialization.md#which-conventions-apply) | runs it | runs it | runs it through a public wrapper of a public base ([AWT234](../09-diagnostics.md#awt234) when there is none) |
| An `internal` `[Inject]` property, or one with an `internal` setter | sets it | sets it | builds the type through a module's generated factory, or reports it ([AWT226](../09-diagnostics.md#awt226)) |
| An implementation nested privately in the base | calls the base's `internal` factory | calls the base's `internal` factory | can't reach the factory |

**Private members:**

- When the base's assembly grants `InternalsVisibleTo` to any assembly, the base generates an `internal` wrapper for each private `Factory` or `Instance` member and lifecycle hook its registrations name, and an extending container in another assembly calls the wrapper. So a private member works there as soon as the test assembly has access.
- A member whose signature names a type private to the base, and a generic factory, get no wrapper.
- An extending container in the base's own assembly doesn't use the wrappers: make the member internal there.

**Async initializer conventions:**

- A convention of the base may be internal even without `InternalsVisibleTo`. A public base generates a public wrapper in its `Internals` class for each convention whose type or method is not public, and an extending container in another assembly runs the convention through it.
- The wrapper takes the parameters of the convention's method, so their types must be public.
- When the base generates no wrapper, because a parameter type is internal or the convention type is private to the base, it records why, and the extending container reports the convention with that cause ([AWT234](../09-diagnostics.md#awt234)). A base built without the Awaiten generator has no wrappers, so its internal conventions are reported the same way.

**Internal injected properties:**

- An `[Inject]` property that is `internal`, or has an `internal` setter, is invisible to an extending container in another assembly without `InternalsVisibleTo`.
- When a module the base imports registers the type, the extending container builds it through the module's generated factory, which sets the property. It calls that factory for the base's own registration of the type too, as it does for a type whose constructor is internal.
- When the base registers, scans, decorates or composes such a type itself, or fills such a property through an `[InjectProperty]` entry of its own, it records the property in its `Internals` class, which is then public, and the extending container reports the type ([AWT226](../09-diagnostics.md#awt226)) instead of building it without the property. Make the property public, or grant the extending container's assembly `InternalsVisibleTo`.
- The base records such a type whether or not a container extends it, so a public base gets a public `Internals` class with a constant per type. It is hidden from IntelliSense and not meant to be used, and its member names are no stable API: an API compatibility check of your own may still list them.

</details>

### Private implementations of the base

An implementation nested privately in the base, or in a class that contains the base, is built by the base. The extending container cannot construct such a type, so the base generates an `internal` factory for it, and the extending container calls that factory. Everything else the registration declares still applies: the lifetime, `Eager`, keys, `WhenInjectedInto`, `Fallback`, collections, lifecycle hooks, async initialization and disposal. A private match of the base's scan is built the same way, so the test graph holds the same services as the production graph.

```csharp
[Container]
[Singleton<BurrGrinder, IGrinder>]
[Scan<IPlugin>(As = ScanAs.Marker)]
public static partial class Roastery
{
    private sealed class BurrGrinder : IGrinder;

    private sealed class AuditPlugin : IPlugin;
}

[Container(Extends = typeof(Roastery))]
public static partial class TestRoastery;   // resolves IGrinder and IEnumerable<IPlugin> like Roastery
```

<details>
<summary>How the base builds its private types for an extending container</summary>

- A container only generates these factories when it has private implementations, and an extending container never does.
- The factory sits in an `internal` nested class named `Internals`, so an extending container in another assembly needs `InternalsVisibleTo`, like for any other internal member of the base.
- A registration the extending container replaces never calls the factory.
- The factory mirrors the constructor the base uses. It copies an `[Arg]` or `[FromKey]` of a constructor parameter onto its own parameter, and takes each injected property as a parameter with the property's name.
- It still can't build every private type. A closed type with an `Optional` or `Deferred` injected property, a hook parameter marked `[Arg]`, `[FromKey]` or `[Inject]`, or a parameter type private to the base is not built for the extending container.
- A private decorator or composite of the base is built through a factory of its own, which takes the inner service or the collection.
- A private open generic registration, decorator or composite of the base is built through a generic factory, which has the open type's type parameters and constraints and which the extending container closes for each closing it uses. It fills every `[Inject]` property, `Optional` and `Deferred` ones included, and each `[InjectProperty]` entry the base declares for the open type. Its lifecycle hooks run through generic wrappers the extending container closes alike.
- An open generic registration with a constraint type private to the base, a hook parameter marked `[Arg]`, `[FromKey]` or `[Inject]`, an `Optional` or `Deferred` `[InjectProperty]` entry, or an `[InjectProperty]` entry for one of its closings, is not built for the extending container.
- Make such a type `internal`. A type nested privately in an imported module, or in a class that contains the module, is fine, because every container builds it through the module's generated factory.

</details>

## One level deep

The base must be another non-generic `[Container]` ([AWT222](../09-diagnostics.md#awt222)), not the container itself ([AWT223](../09-diagnostics.md#awt223)), and it must not extend a container of its own ([AWT224](../09-diagnostics.md#awt224)). Extending stays one level deep, so every registration of an extending container comes either from its own declaration or from its one base.

:::tip[When not to reach for this]
`Extends` is meant for tests and similar variants of one composition, such as a container for a local demo that swaps the payment terminal. It is not a way to build an inheritance hierarchy of production containers: shared wiring between several production containers belongs in [modules](./08-modules/index.md), which each container imports explicitly.

Keep unit tests free of containers altogether. A unit test constructs the class under test with its fakes directly; `Extends` is for the composition, integration and host-level tests that want the real graph. A test container that replaces half of its base is a hint that the production container composes too much in one place. See [Design principles](../03-design-principles.md#the-composition-root).
:::

## Where to go next

- [Modules](./08-modules/index.md) to share registrations between containers.
- [External services](../07-hosting/02-external-services.mdx) for services the host supplies.
- [Design principles](../03-design-principles.md) for the composition root a test container reuses.
