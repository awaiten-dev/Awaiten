---
description: Every Awaiten attribute with the place it goes, its forms and its properties with their defaults, plus the enums the attributes take.
toc_max_heading_level: 3
---

# Attribute reference

All attributes live in the `Awaiten` namespace. A property you do not set keeps the default listed here. The **See** column links to the page that explains the option.

Most attributes take a generic form and a `typeof` form. The generic form reads better and the compiler checks the type constraints. The `typeof` form also accepts an unbound generic such as `typeof(Repository<>)`, which cannot be a type argument, and works below C# 11 (see [Requirements](../01-getting-started.md#requirements)).

## Containers and modules

### Container

Marks a `static partial` class as a container. The generator adds the nested `Root` and `Scope` types to it. See [Getting started](../01-getting-started.md#build-your-first-container).

```csharp no-compile
[Container]
[Container(AsyncSafety = AsyncSafety.SyncAfterInit, LifetimeSafety = LifetimeSafety.Loose)]
[Container(Extends = typeof(CoffeeShop))]
```

| Property | Type | Default | Meaning | See |
|---|---|---|---|---|
| `AsyncSafety` | [`AsyncSafety`](#asyncsafety) | `Strict` | Whether an async-initialized service is reachable only asynchronously, or also synchronously after warm-up. | [Async initialization](../04-core-concepts/03-async-initialization.md#relax-the-rule-when-you-need-to) |
| `LifetimeSafety` | [`LifetimeSafety`](#lifetimesafety) | `Strict` | Whether the root withholds disposable transients and a root-owned `Func` over one is an error. | [Lifetime safety](../04-core-concepts/04-lifetime-safety.md) |
| `Extends` | `Type?` | `null` | Another container whose whole composition this one starts from. The base's options apply, so a container that sets `Extends` does not set `AsyncSafety` or `LifetimeSafety` itself ([AWT225](../09-diagnostics.md#awt225)). The base must not extend another container ([AWT224](../09-diagnostics.md#awt224)). | [Extending containers](../05-registration/09-extending-containers.md) |

Goes on a class once. The class must be `static` ([AWT116](../09-diagnostics.md#awt116)).

### Module

Marks a `static` class as a reusable set of registrations that a container pulls in with `[Import]`. A module carries the same registration attributes as a container. It has no properties. The class must be `static` ([AWT152](../09-diagnostics.md#awt152)). See [Modules](../05-registration/08-modules/index.md#declare-a-module).

```csharp no-compile
[Module]
```

Goes on a class once. A module in a library that other assemblies import is `partial` as well, so the generator can add the code those containers need. See [Shipping modules from a library](../05-registration/08-modules/01-library-modules.md).

### Import

Pulls a module's registrations into a container. The container's own registrations take precedence over imported ones. Imports are followed one level deep: a module with its own `[Import]` is an error ([AWT150](../09-diagnostics.md#awt150)). See [Modules](../05-registration/08-modules/index.md#import-it).

```csharp no-compile
[Import(typeof(PaymentModule))]
```

| Property | Type | Default | Meaning | See |
|---|---|---|---|---|
| `Module` | `Type` | constructor argument | The module to import. | [Modules](../05-registration/08-modules/index.md#import-it) |

Goes on a container, any number of times.

## Registrations

### Singleton, Scoped and Transient

Register an implementation with a [lifetime](../04-core-concepts/01-lifetimes.md). The three attributes share their forms and properties, except that `Eager` and `Instance` exist only on `[Singleton]`.

```csharp no-compile
[Singleton<Grinder>]                                   // the implementation is its own service
[Scoped<CardTerminal, IPaymentTerminal>]               // implementation first, then the service
[Transient(typeof(Repository<>), typeof(IRepository<>))]   // typeof form, here an open generic
[Singleton(typeof(Grinder))]                           // typeof form, its own service
```

| Form | Registers |
|---|---|
| `[Singleton<TImplementation>]` | `TImplementation` as itself. With `Factory` or `Instance` it may be an interface or abstract type, since the container constructs nothing. |
| `[Singleton<TImplementation, TService>]` | `TImplementation` under `TService`. `TImplementation` must derive from or implement `TService`. |
| `[Singleton(typeof(Impl))]` | Like the first form. Accepts an unbound generic. |
| `[Singleton(typeof(Impl), typeof(Service))]` | Like the second form. With unbound generics, each closing the graph asks for is built separately. See [Open generics](../05-registration/06-open-generics.md). |

| Property | Type | Default | Meaning | See |
|---|---|---|---|---|
| `Implementation` | `Type` | constructor argument | `typeof` form only: the type to construct. | [Open generics](../05-registration/06-open-generics.md#register-the-open-type) |
| `Service` | `Type` | constructor argument, else `Implementation` | `typeof` form only: the type the instance resolves as. | [Lifetimes](../04-core-concepts/01-lifetimes.md#register-against-a-service-type) |
| `Factory` | `string?` | `null` | Name of a method of the container or module that produces the instance instead of a constructor. Its parameters resolve from the graph. On an open generic it names a generic method with the service's type parameters. | [Factories](../05-registration/01-factories-and-instances.md#factory) |
| `Instance` | `string?` | `null` | `[Singleton]` only: name of a field or property that holds a pre-built instance. The container neither constructs nor disposes it. Needs a closed type. | [Instances](../05-registration/01-factories-and-instances.md#instance) |
| `Key` | `object?` | `null` | Registers under a key, so several implementations can share a service type. A consumer selects one with `[FromKey]`. | [Keyed services](../05-registration/05-keyed-services.md#register-with-a-key) |
| `WhenInjectedInto` | `Type?` | `null` | Uses this registration only for the constructor parameters and `[Inject]` properties of the given consumer type, and the unconditional registration everywhere else. | [Contextual binding](../05-registration/05-keyed-services.md#bind-a-variant-to-a-consumer-with-wheninjectedinto) |
| `Fallback` | [`Fallback`](#fallback) | `None` | Makes the registration an overridable default that a registration of the same service without `Fallback` replaces, unless that is a scan match or the closing of an open generic over a closed default. | [Overridable defaults](../05-registration/08-modules/index.md#overridable-defaults) |
| `Eager` | `bool` | `false` | `[Singleton]` only: builds the singleton when the root is created instead of on first resolve. Needs a closed type. | [Lifetimes](../04-core-concepts/01-lifetimes.md#build-a-singleton-at-startup) |
| `OnActivated` | `string?` | `null` | Name of a static method that receives the instance once it is constructed. A method returning `Task` or `ValueTask` makes the service async-initialized. | [Lifecycle hooks](../05-registration/02-lifecycle-hooks.md#onactivated-and-onrelease) |
| `OnRelease` | `string?` | `null` | Name of a static method that receives the instance when its owning container or scope is disposed, before the instance's own disposal. A method returning `Task` or `ValueTask` is awaited, which needs asynchronous disposal. | [Lifecycle hooks](../05-registration/02-lifecycle-hooks.md#onactivated-and-onrelease) |
| `SuppressDisposal` | `bool` | `false` | Builds the instance but never disposes it. Releasing it is up to you, typically through `OnRelease`. | [Disposal](../04-core-concepts/05-disposal.md#suppressing-disposal) |

Go on a container or module, any number of times.

### Decorate

Wraps every resolution of a service in a decorator. The decorator takes the inner instance in a constructor parameter of the service or of a base interface of it. When several parameters accept the service, the one whose type converts to all the others receives it ([AWT124](../09-diagnostics.md#awt124)). The decorator inherits the lifetime of what it wraps. See [Decorators](../05-registration/03-decorators.md#wrap-a-service).

```csharp no-compile
[Decorate<LoggingBrewer, IBrewer>]
[Decorate<LoggingTerminal, IPaymentTerminal>(Keys = new object?[] { null, "backup" }, Order = 1)]
[Decorate(typeof(LoggingBehavior<>), typeof(IHandler<>))]   // every closing of an open generic
```

| Property | Type | Default | Meaning | See |
|---|---|---|---|---|
| `Decorator` | `Type` | constructor argument | `typeof` form only: the open generic decorator. | [Decorators](../05-registration/03-decorators.md#decorate-every-closing-of-an-open-generic) |
| `Service` | `Type` | constructor argument | `typeof` form only: the open generic service whose closings it wraps. | [Decorators](../05-registration/03-decorators.md#decorate-every-closing-of-an-open-generic) |
| `Order` | `int` | `0` | Position in the chain, ascending, outermost last. Ties keep declaration order, where the last declared is outermost. | [Chain several](../05-registration/03-decorators.md#chain-several) |
| `Keys` | `object?[]?` | `null` | The keys whose registrations to decorate. A `null` entry selects the unkeyed registrations. Unset, only the unkeyed registrations are decorated. Cannot combine with `AllKeys`. | [Keyed registrations](../05-registration/03-decorators.md#keyed-registrations) |
| `AllKeys` | `bool` | `false` | Decorates the unkeyed registrations and those under every key. Cannot combine with `Keys`. | [Keyed registrations](../05-registration/03-decorators.md#keyed-registrations) |
| `When` | `string?` | `null` | Name of a static `bool` method, property or field on the declaring type. When it is `false`, the decorator is never built. It is evaluated once per root. | [Apply a decorator conditionally](../05-registration/03-decorators.md#apply-a-decorator-conditionally) |

Goes on a container or module, any number of times.

### Composite

Exposes one implementation as the single service and hands it every other registration of that service through a collection constructor parameter. See [Composites](../05-registration/04-composites.md#compose-a-service).

```csharp no-compile
[Composite<CompositeReceiptChannel, IReceiptChannel>]
[Composite<CompositeReceiptChannel, IReceiptChannel>(Key = "night", Lifetime = Lifetime.Singleton)]
[Composite(typeof(CompositeHandler<>), typeof(IHandler<>))]   // every closing of an open generic
```

| Property | Type | Default | Meaning | See |
|---|---|---|---|---|
| `Composite` | `Type` | constructor argument | `typeof` form only: the open generic composite. | [Composites](../05-registration/04-composites.md#compose-every-closing-of-an-open-generic) |
| `Service` | `Type` | constructor argument | `typeof` form only: the open generic service it fronts. | [Composites](../05-registration/04-composites.md#compose-every-closing-of-an-open-generic) |
| `Lifetime` | [`Lifetime`](#lifetime) | `Transient` | The composite's own lifetime. Members keep theirs. | [Composites](../05-registration/04-composites.md#lifetime) |
| `Key` | `object?` | `null` | The key whose registrations the composite fronts. Unset, it fronts the unkeyed registrations. | [Front a key](../05-registration/04-composites.md#front-a-key) |

Goes on a container or module, any number of times.

### Scan

Registers every concrete class that matches, from metadata the compiler reads at build time. See [Scanning](../05-registration/07-scanning.md#scan-for-a-marker).

```csharp no-compile
[Scan<IDrink>]                                         // classes assignable to the marker
[Scan(typeof(IScreen<>))]                              // classes implementing a closing of an open marker
[Scan(NamePatterns = new[] { "*Handler" })]            // markerless: matched by the filters alone
```

| Property | Type | Default | Meaning | See |
|---|---|---|---|---|
| `AssignableTo` | `Type?` | constructor argument | The marker interface or base type passed as `typeof` to the non-generic `[Scan]`, or `null` for its parameterless, markerless form. | [Scan without a marker](../05-registration/07-scanning.md#scan-without-a-marker) |
| `Lifetime` | [`Lifetime`](#lifetime) | `Transient` | The lifetime of every match. | [Choose the lifetime](../05-registration/07-scanning.md#choose-the-lifetime) |
| `As` | [`ScanAs`](#scanas) | `Self` | How each match registers. | [Choose how they register](../05-registration/07-scanning.md#choose-how-they-register) |
| `InAssembliesOf` | `Type[]?` | `null` | Scans the assemblies of these types instead of the container's own assembly. | [Scan other assemblies](../05-registration/07-scanning.md#scan-other-assemblies) |
| `NamePatterns` | `string[]?` | `null` | Globs on the simple type name. `*` matches any run of characters, a `!` prefix excludes. | [Narrow the matches](../05-registration/07-scanning.md#narrow-the-matches) |
| `NamespacePatterns` | `string[]?` | `null` | Globs on the namespace, by segment: `*` matches one segment, `**` any number. A `!` prefix excludes. | [Narrow the matches](../05-registration/07-scanning.md#narrow-the-matches) |
| `Exclude` | `Type[]?` | `null` | Types to leave out, by exact identity. | [Narrow the matches](../05-registration/07-scanning.md#narrow-the-matches) |
| `SkipUnconstructable` | `bool` | `false` | Skips a match the container cannot construct with the warning [AWT141](../09-diagnostics.md#awt141) instead of the error [AWT101](../09-diagnostics.md#awt101). | [Leave out types that are not services](../05-registration/07-scanning.md#leave-out-types-that-are-not-services) |
| `OnActivated` | `string?` | `null` | A static hook that receives each match once it is constructed. | [Lifecycle hooks](../05-registration/07-scanning.md#lifecycle-hooks) |
| `OnRelease` | `string?` | `null` | A static hook that receives each match when its owner is disposed. | [Lifecycle hooks](../05-registration/07-scanning.md#lifecycle-hooks) |

Goes on a container or module, any number of times. A markerless scan cannot use `ScanAs.Marker` and needs an `InAssembliesOf`, or a `NamePatterns` or `NamespacePatterns` filter whose patterns each name something: a filter of wildcards only, such as `"*"` or `"**"`, matches everything and does not count ([AWT183](../09-diagnostics.md#awt183)).

### AsyncInitializer

Declares an async initializer convention: every service of the container or module whose implementation implements the interface or derives from the class `T` is initialized by awaiting the named method once, before it is handed out. The method is an instance method of `T` that returns `Task` or `ValueTask`, is not overloaded, and has its parameters resolved from the graph. A `CancellationToken` parameter receives the token of the call that triggered the initialization. See [Async initialization](../04-core-concepts/03-async-initialization.md#mark-a-service-as-async).

```csharp no-compile
[AsyncInitializer<IWarmUp>(nameof(IWarmUp.WarmUpAsync))]
```

| Property | Type | Default | Meaning | See |
|---|---|---|---|---|
| `Method` | `string` | constructor argument | The initializing method of `T`. | [Which conventions apply](../04-core-concepts/03-async-initialization.md#which-conventions-apply) |

Generic form only. Goes on a container or module, any number of times. A convention applies to the registrations of the container or module that declares it.

## Host services

### ImportService

Declares a service the host provides: every direct dependency of that type, keyed or not, is resolved from the container's [external resolver](../07-hosting/02-external-services.mdx#providing-the-external-resolver) instead of being reported as missing. A relationship or collection over it is not routed. See [External services](../07-hosting/02-external-services.mdx#pull-a-single-type-with-importservice).

```csharp no-compile
[ImportService<IPaymentTerminal>]
[ImportService(typeof(ILogger<>))]   // every closing of a generic type
```

| Property | Type | Default | Meaning | See |
|---|---|---|---|---|
| `Service` | `Type` | constructor argument | `typeof` form only: a closed type, or an unbound generic whose closings are all routed. | [Pull every closing](../07-hosting/02-external-services.mdx#pull-every-closing-of-a-generic-type) |

Goes on a container or module, any number of times. Registering the same type on the container as well is an error ([AWT175](../09-diagnostics.md#awt175)).

### ImportServices

Routes every unkeyed direct dependency that has no registration to the external resolver. It trades the compile-time missing-dependency check for flexibility, so prefer `[ImportService]` per type. It has no properties. See [External services](../07-hosting/02-external-services.mdx#fall-through-everything-with-importservices).

```csharp no-compile
[ImportServices]
```

Goes on a container or module, once. Keyed dependencies, relationships and injected properties are not routed.

## Injection sites

### Inject

Marks a settable or `init` property of an implementation to fill from the graph after construction. The property is required unless it is `Optional`. See [Property injection](../06-resolution/05-property-injection.md#the-escape-hatch-inject-on-the-property).

```csharp no-compile
[Inject] public ILogger Logger { get; init; }
[Inject(Optional = true, Deferred = true)] public IAudit? Audit { get; set; }
```

| Property | Type | Default | Meaning | See |
|---|---|---|---|---|
| `Optional` | `bool` | `false` | Leaves the property at its default when nothing is registered, instead of [AWT101](../09-diagnostics.md#awt101). | [Optional](../06-resolution/05-property-injection.md#tolerate-a-missing-service-with-optional) |
| `Deferred` | `bool` | `false` | Assigns the property after the instance is constructed and cached, which can break a constructor cycle. Needs a plain `set` accessor. | [Deferred](../06-resolution/05-property-injection.md#break-a-cycle-with-deferred) |

Goes on a property, once. Combine with `[FromKey]` to select a keyed registration.

### InjectProperty

The container-side counterpart of `[Inject]`: names a property of an implementation to fill, so the implementation stays free of Awaiten attributes. It applies wherever the implementation is constructed, scans included. See [Property injection](../06-resolution/05-property-injection.md#declare-it-on-the-container-with-injectpropertyt).

```csharp no-compile
[InjectProperty<OrderService>(nameof(OrderService.Logger))]
[InjectProperty(typeof(Repository<>), nameof(Repository<object>.Logger))]   // every closing
```

| Property | Type | Default | Meaning | See |
|---|---|---|---|---|
| `Implementation` | `Type` | constructor argument | `typeof` form only: a closed type or an unbound generic. | [Open generics](../06-resolution/05-property-injection.md#open-generics-with-the-typeof-form) |
| `PropertyName` | `string` | constructor argument | The property to fill. Use `nameof`. | [Property injection](../06-resolution/05-property-injection.md#declare-it-on-the-container-with-injectpropertyt) |
| `Optional` | `bool` | `false` | As on `[Inject]`. | [Optional](../06-resolution/05-property-injection.md#tolerate-a-missing-service-with-optional) |
| `Deferred` | `bool` | `false` | As on `[Inject]`. | [Deferred](../06-resolution/05-property-injection.md#break-a-cycle-with-deferred) |
| `Key` | `object?` | `null` | Selects the keyed registration of the property's type, like `[FromKey]`. | [Key](../06-resolution/05-property-injection.md#select-a-keyed-registration-with-key) |

Goes on a container, once per property.

### FromKey

Selects the keyed registration for a constructor parameter, factory or hook parameter, or injected property. See [Keyed services](../05-registration/05-keyed-services.md#select-directly-with-fromkey).

```csharp no-compile
public sealed class Latte([FromKey("Oat")] IMilk milk);
public sealed class Checkout([FromKey(MilkKind.Oat)] IMilk milk);   // an enum constant as the key
```

| Property | Type | Default | Meaning | See |
|---|---|---|---|---|
| `Key` | `object` | constructor argument | The key to select: a string, an `enum` value or a `typeof(...)`. | [Typed keys](../05-registration/05-keyed-services.md#typed-keys) |

Goes on a parameter or property, once.

### Arg

Marks a parameter of a `Factory` method or of a constructor as a runtime argument. The factory form keeps the produced type free of Awaiten. A service with `[Arg]` parameters is built fresh on every call and resolves only through a `Func` whose leading type arguments supply the marked parameters in order: `Func<TArg…, T>`, `Func<TArg…, Owned<T>>`, `Func<TArg…, Task<T>>` or `Func<TArg…, Task<Owned<T>>>`. It has no properties. See [Runtime arguments](../06-resolution/06-runtime-arguments.md#mark-a-parameter-with-arg).

```csharp no-compile
private static Cup MakeCup(EspressoMachine machine, [Arg] string size) => new(machine, size);   // factory form
public sealed class Cup(EspressoMachine machine, [Arg] string size);                            // constructor form
```

Goes on a parameter, once.

### RequestingType

Marks a `Type?` parameter of a `Factory` method that receives the type being constructed, such as the consumer of a logger. It is `null` when the service is resolved straight from the container. It has no properties. See [Context-aware factories](../06-resolution/07-context-aware-factories.md#ask-for-the-requesting-type).

```csharp no-compile
private static ILogger CreateLogger([RequestingType] Type? consumer) => /* … */;
```

Goes on a parameter, once. A parameter that is not `Type` is [AWT162](../09-diagnostics.md#awt162).

## Enums

### Lifetime

| Value | Meaning |
|---|---|
| `Singleton` (0) | One instance for the life of the container. |
| `Scoped` (1) | One instance per scope. The root acts as the outermost scope. |
| `Transient` (2) | A new instance for every resolve or injection. |

Used by `[Composite]` and `[Scan]`, and reported in [`AwaitenRegistration.Lifetime`](./02-runtime-api.md#registration-metadata). The values match `ServiceLifetime` of Microsoft.Extensions.DependencyInjection. See [Lifetimes](../04-core-concepts/01-lifetimes.md).

### AsyncSafety

| Value | Meaning |
|---|---|
| `Strict` (0, default) | An async-initialized service is reachable only asynchronously, through `ResolveAsync` or an awaiting shape such as `Task<T>`. A synchronous shape over one is a compile-time error, and a synchronous resolve throws. |
| `SyncAfterInit` (1) | An async-initialized service also resolves synchronously. After `InitializeAsync` warmed it, that returns at once; before, it blocks until the initialization completes. |

See [Async initialization](../04-core-concepts/03-async-initialization.md#relax-the-rule-when-you-need-to).

### LifetimeSafety

| Value | Meaning |
|---|---|
| `Strict` (0, default) | A disposable transient is withheld from by-type resolution on the root, and a root-owned `Func` over one is the error [AWT118](../09-diagnostics.md#awt118). Constructor injection, child scopes and `Owned<T>` still reach it. |
| `Loose` (1) | Everything resolves everywhere, as in Microsoft.Extensions.DependencyInjection. AWT118 becomes a warning. |

See [Lifetime safety](../04-core-concepts/04-lifetime-safety.md).

### Fallback

| Value | Meaning |
|---|---|
| `None` (0, default) | A normal registration. |
| `Warn` (1) | An overridable default. When another `Warn` default provides the same service and nothing stronger decides, [AWT148](../09-diagnostics.md#awt148) warns and the first declared wins. |
| `Silent` (2) | An overridable default that defers without a warning. |

See [Overridable defaults](../05-registration/08-modules/index.md#overridable-defaults).

### ScanAs

A flags enum: combine values with the `|` operator, such as `ScanAs.Marker | ScanAs.Self`.

| Value | Meaning |
|---|---|
| `Self` (1, default) | Registers each match as its own type. |
| `Marker` (2) | Registers each match under the scanned marker, which makes the matches a collection of the marker. |
| `MatchingInterface` (4) | Registers each match under the interface named `I` plus its own name, such as `IOrderService` for `OrderService`. |

See [Choose how they register](../05-registration/07-scanning.md#choose-how-they-register).

## Where to go next

- [Runtime API](./02-runtime-api.md) for what you call on a container.
- [Diagnostics](../09-diagnostics.md) for every check the attributes are subject to.
