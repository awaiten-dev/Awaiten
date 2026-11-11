---
description: Short definitions of the terms the Awaiten documentation uses, each linking to the page that explains it.
toc_max_heading_level: 3
---

# Glossary

The terms the documentation uses, in alphabetical order. Each entry links to the page that explains it in full.

### Activation hook

A static method named with `OnActivated` that receives a service once it is constructed, before anyone gets it. A hook that returns `Task` or `ValueTask` makes the service async-initialized. See [Lifecycle hooks](../05-registration/02-lifecycle-hooks.md).

### Async initializer convention

An `[AsyncInitializer<T>]` on a container or module that names a method of `T`, such as `IWarmUp.WarmUpAsync`. Every service of that container or module whose implementation is a `T` is initialized by awaiting the method once, before it is handed out. Your domain type keeps no reference to Awaiten. See [Async initialization](../04-core-concepts/03-async-initialization.md#mark-a-service-as-async).

### Async-initialized

A service that needs asynchronous setup before use: an async initializer convention or an asynchronous activation hook initializes it, or an async factory that returns `Task<T>` produces it. See [Async initialization](../04-core-concepts/03-async-initialization.md).

### Async-tainted

A service that is async-initialized or depends on one through a dependency that is not deferred, such as a plain constructor parameter. Under strict async safety it is reachable only asynchronously. A deferred shape such as `Task<T>` or `Func<Task<T>>` keeps the consumer synchronous. See [Keep a consumer synchronous by deferring the async work](../04-core-concepts/03-async-initialization.md#keep-a-consumer-synchronous-by-deferring-the-async-work).

### Async safety

The `AsyncSafety` option of a container. `Strict`, the default, makes an async-tainted service reachable only asynchronously. `SyncAfterInit` also lets it resolve synchronously, which blocks until it is initialized when it was not warmed up. See [Relax the rule](../04-core-concepts/03-async-initialization.md#relax-the-rule-when-you-need-to).

### Base container

A container that another one names in `[Container(Extends = …)]`. The extending container starts from its whole composition. See [Extending containers](../05-registration/09-extending-containers.md).

### Captive dependency

A shorter-lived service held by a longer-lived one, such as a scoped `Order` inside a singleton. It would outlive its scope, so Awaiten reports it at build time ([AWT105](../09-diagnostics.md#awt105)). See [Lifetimes must line up](../04-core-concepts/01-lifetimes.md#lifetimes-must-line-up).

### Closing

A closed form of an open generic registration, such as `IRepository<Order>` for `[Transient(typeof(Repository<>), typeof(IRepository<>))]`. Awaiten builds only the closings the graph asks for. See [Open generics](../05-registration/06-open-generics.md#resolve-a-closed-type).

### Collection

A dependency on every registration of a service, as `IEnumerable<T>`, `IReadOnlyList<T>`, an array or another supported shape. Awaiten builds it from the registrations at build time. See [Collections](../06-resolution/03-collections.md).

### Composite

An implementation that stands in for all others of a service: a plain dependency on the service gets the composite, and the composite receives the other registrations as a collection. Declared with `[Composite]`. See [Composites](../05-registration/04-composites.md).

### Composition root

The one place in an application that composes the object graph. In Awaiten that is your `[Container]` and the `Root` you create at the entry point. Domain code does not reference the container. See [Design principles](../03-design-principles.md#the-composition-root).

### Container

A `static partial` class marked `[Container]` that declares the registrations. The generator completes it with the `Root` and `Scope` types that do the work. See [Getting started](../01-getting-started.md#build-your-first-container).

### Contextual binding

A registration that applies only where it is injected into a given consumer, declared with `WhenInjectedInto`. Every other consumer gets the unconditional registration. See [Keyed services](../05-registration/05-keyed-services.md#bind-a-variant-to-a-consumer-with-wheninjectedinto).

### Decorator

An implementation that wraps a service: it takes the service as a constructor parameter and is handed out in its place. Declared with `[Decorate]`. Several decorators chain. See [Decorators](../05-registration/03-decorators.md).

### Dispatch dictionary

A dictionary keyed by `Type`, such as `IReadOnlyDictionary<Type, Func<IOrderHandler>>`, that maps the type argument of each closing of a generic handler to a factory for that closing, for dispatch on a type only known at run time. See [Dispatch by runtime type](../06-resolution/04-keyed-dictionaries.md#dispatch-by-runtime-type).

### Eager singleton

A singleton marked `Eager = true`, built when the root is created instead of on first resolve. See [Build a singleton at startup](../04-core-concepts/01-lifetimes.md#build-a-singleton-at-startup).

### Extending container

A container that sets `Extends` to start from the composition of a base container, typically a test container that replaces a few services of the production one. See [Extending containers](../05-registration/09-extending-containers.md).

### External resolver

The `IExternalResolver` a container asks for its imported services. The MS.DI bridge wires one over the host's provider, and a standalone container passes its own to the `Root` constructor. See [Providing the external resolver](../07-hosting/02-external-services.mdx#providing-the-external-resolver).

### Generated handle

A public type a library module generates so that a container in another assembly can build, share and dispose a service registered under a type internal to the library. A handle is a dependency only and never resolved directly. See [Shipping modules from a library](../05-registration/08-modules/02-library-module-rules.md#handles).

### Implementation

The concrete type the container constructs for a registration, such as `CardTerminal` in `[Scoped<CardTerminal, IPaymentTerminal>]`. Compare [service type](#service-type). See [Lifetimes](../04-core-concepts/01-lifetimes.md#register-against-a-service-type).

### Imported service

A service the host provides rather than the container, declared with `[ImportService]` or `[ImportServices]`. The container resolves it through its external resolver and never disposes it. See [External services](../07-hosting/02-external-services.mdx).

### Key

A value that tells several registrations of one service apart, set with `Key` on the registration and selected with `[FromKey]` or a keyed `Resolve`. A string, an `enum` value or a `typeof(...)`. See [Keyed services](../05-registration/05-keyed-services.md).

### Keyed dictionary

A dependency such as `IReadOnlyDictionary<string, IMilk>` that maps each key to its keyed registration of a service. See [Keyed dictionaries](../06-resolution/04-keyed-dictionaries.md).

### Lifetime

How long an instance lives and who shares it: a singleton once per container, a scoped service once per scope, a new transient for every resolve or injection. See [Lifetimes](../04-core-concepts/01-lifetimes.md).

### Lifetime safety

The `LifetimeSafety` option of a container. `Strict`, the default, keeps disposable transients from piling up on the root: the root withholds them, and a root-owned `Func` over one is an error. `Loose` resolves everything everywhere, as Microsoft.Extensions.DependencyInjection does. See [Lifetime safety](../04-core-concepts/04-lifetime-safety.md).

### Marker

The interface or base type a `[Scan]` matches against, such as `IDrink` in `[Scan<IDrink>]`. With `ScanAs.Marker` the matches also register under it. See [Scanning](../05-registration/07-scanning.md#scan-for-a-marker).

### Module

A static class marked `[Module]` that carries registrations for a container to `[Import]`. It is the Awaiten counterpart of an `AddXyz()` extension method on `IServiceCollection`. See [Modules](../05-registration/08-modules/index.md).

### MS.DI bridge

The `Awaiten.Extensions.DependencyInjection` package, which runs a container under Microsoft.Extensions.DependencyInjection, such as in ASP.NET Core and the generic host. See [MS.DI bridge](../07-hosting/01-msdi-bridge.md).

### Overridable default

A registration with `Fallback = Fallback.Warn` or `Fallback.Silent`, usually in a module. A registration of the same service without `Fallback` replaces it. See [Overridable defaults](../05-registration/08-modules/index.md#overridable-defaults).

### `Owned<T>`

A disposal handle: the service is built in a throwaway scope that disposing the handle disposes. The leak-free way to get a disposable service on demand. See [Owned](../04-core-concepts/06-owned.md).

### Projection

What the MS.DI bridge does with `AddAwaitenContainer`: it adds a service descriptor to the `IServiceCollection` for each service type and key of the container, the registration that wins single resolution, so the host resolves Awaiten services side by side with its own. Collections are not projected. See [Project into a service collection](../07-hosting/01-msdi-bridge.md#project-into-a-service-collection).

### Registration

An attribute such as `[Singleton<Grinder>]` that tells the container how to provide a service: which implementation, with which lifetime, and with which options. See [Lifetimes](../04-core-concepts/01-lifetimes.md) and the [attribute reference](./01-attributes.md).

### Relationship

A dependency shape that wraps a single service: `Func<T>` builds on demand, `Lazy<T>` builds once on first use, `Task<T>` awaits an async service, and `Owned<T>` hands over disposal. See [Relationships](../06-resolution/02-relationships.md).

### Release hook

A static method named with `OnRelease` that receives a service when its owning container or scope is disposed, before the service's own disposal. See [Lifecycle hooks](../05-registration/02-lifecycle-hooks.md).

### Root

The generated `Root` type of a container, which you create with `new`. It is the container instance, the outermost scope and the owner of the singletons. See [Scopes](../04-core-concepts/02-scopes.md#the-root-is-the-outermost-scope).

### Scan

A `[Scan]` that registers every matching concrete class, read from the compilation at build time instead of listing each one. See [Scanning](../05-registration/07-scanning.md).

### Scope

A unit of work, such as one order or one web request, that caches its scoped services and disposes what it built. You open one from the root with `CreateScope` or `CreateScopeAsync`. See [Scopes](../04-core-concepts/02-scopes.md).

### Service type

The type a registration is resolved as, such as `IPaymentTerminal` in `[Scoped<CardTerminal, IPaymentTerminal>]`. With one type argument, the implementation is its own service type. See [Lifetimes](../04-core-concepts/01-lifetimes.md#register-against-a-service-type).

### Warm-up

Building and initializing async services ahead of time: `InitializeAsync` on the root for singletons, `CreateScopeAsync` for the scoped services of a new scope. See [Warm everything up front](../04-core-concepts/03-async-initialization.md#warm-everything-up-front).

### Withheld service

A service the container has but does not hand out through a given call, such as a disposable transient on the root under strict lifetime safety, or an async-tainted service on a synchronous `Resolve`. The exception names the fix, and `GetResolvability` reports why without throwing. See [Feature detection](../07-hosting/01-msdi-bridge.md#feature-detection).
