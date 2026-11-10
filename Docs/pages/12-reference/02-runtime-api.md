---
description: The public runtime surface of Awaiten, from the generated Root and Scope and the resolver interfaces to Owned, external resolvers, registration metadata and the MS.DI bridge.
toc_max_heading_level: 3
---

# Runtime API

This page lists what you call at run time. The types are in the `Awaiten` namespace, and the MS.DI bridge types are in `Awaiten.Extensions.DependencyInjection`. Signatures are shortened: `CancellationToken cancellationToken = default` is written `ct = default`.

## The generated Root and Scope

The generator adds two public types to every container. See [What Awaiten generates](../02-complete-example/01-generated-code.md).

| Member | What it does | See |
|---|---|---|
| `new MyContainer.Root()` | Creates the container. It is the outermost scope and owns the singletons. Eager singletons are built here. | [Getting started](../01-getting-started.md#build-your-first-container) |
| `new MyContainer.Root(IExternalResolver? externalResolver)` | Creates the container with the resolver its imported services come from, in place before anything is resolved. | [External services](../08-hosting/02-external-services.mdx#providing-the-external-resolver) |
| `Root : Scope, IAwaitenRoot` | The root implements the members of [`IAwaitenRoot`](#iawaitenroot) and everything a scope offers. | |
| `Scope : IAwaitenScope, IAwaitenExternalResolverHost` | A child scope. It implements [`IAwaitenScope`](#iawaitenscope). | [Scopes](../04-core-concepts/02-scopes.md) |
| `Scope CreateScope()` | Opens a child scope, typed as the generated `Scope`. | [Scopes](../04-core-concepts/02-scopes.md#open-a-scope) |
| `Task<Scope> CreateScopeAsync(ct = default)` | Opens a child scope and warms its async scoped services first. | [Async initialization](../04-core-concepts/03-async-initialization.md#warm-a-scope) |
| `T Resolve<T>()` | An instance method on `Root` and `Scope` with the same behavior as the extension method below. | [Resolving services](../06-resolution/01-resolving-services.md#resolve) |

A typed resolve on a receiver declared as `Root` or `Scope` is checked at build time ([AWT218](../09-diagnostics.md#awt218), [AWT219](../09-diagnostics.md#awt219)). A call through an interface is checked only at run time.

## Resolving

### IAwaitenResolver

The synchronous surface of the root, every scope and adapters such as the MS.DI bridge.

| Member | What it does | See |
|---|---|---|
| `object Resolve(Type serviceType)` | Resolves the service, or throws an `InvalidOperationException`. For a service the container has but withholds from this call, the message names the fix. | [Resolve](../06-resolution/01-resolving-services.md#resolve) |
| `object Resolve(Type serviceType, object? key)` | Resolves the registration under `key`. A `null` key resolves the unkeyed registration. | [Keyed resolution](../06-resolution/01-resolving-services.md#keyed-resolution) |
| `bool TryResolve(Type serviceType, out object? instance)` | Returns `false` instead of throwing when the service has no synchronous path here, such as an unregistered type, an async-initialized service in the strict default or a service the root withholds. | [TryResolve](../06-resolution/01-resolving-services.md#tryresolve) |
| `bool TryResolve(Type serviceType, object? key, out object? instance)` | The keyed form of `TryResolve`. | [Keyed resolution](../06-resolution/01-resolving-services.md#keyed-resolution) |

### IAwaitenAsyncResolver

Extends `IAwaitenResolver` with asynchronous resolution. The root and every scope implement it.

| Member | What it does | See |
|---|---|---|
| `Task<object> ResolveAsync(Type serviceType, ct = default)` | Resolves the service and awaits its async initialization and that of its dependencies, each exactly once. A service without async initialization completes synchronously. Throws when the service is not registered. | [ResolveAsync](../06-resolution/01-resolving-services.md#resolveasync) |
| `Task<object> ResolveAsync(Type serviceType, object? key, ct = default)` | The keyed form. | [Keyed resolution](../06-resolution/01-resolving-services.md#keyed-resolution) |

### AwaitenResolverExtensions

Generic conveniences over the interfaces above.

| Member | What it does |
|---|---|
| `T Resolve<T>()` on `IAwaitenResolver` | `Resolve(typeof(T))`, cast to `T`. |
| `T Resolve<T>(object key)` on `IAwaitenResolver` | The keyed form. |
| `bool TryResolve<T>(out T? instance)` on `IAwaitenResolver` | `TryResolve(typeof(T), …)`, cast to `T`. |
| `bool TryResolve<T>(object key, out T? instance)` on `IAwaitenResolver` | The keyed form. |
| `Task<T> ResolveAsync<T>(ct = default)` on `IAwaitenAsyncResolver` | `ResolveAsync(typeof(T), …)`, typed as `T`. |
| `Task<T> ResolveAsync<T>(object key, ct = default)` on `IAwaitenAsyncResolver` | The keyed form. |
| `AwaitenResolvability GetResolvability(Type serviceType)` on `IAwaitenRoot` | `GetResolvability(serviceType, null)`. |
| `AwaitenResolvability GetResolvability<T>()` on `IAwaitenRoot` | `GetResolvability(typeof(T), null)`. |

A keyed resolve serves plain services only, not a collection, a relationship or the factory of a service with runtime arguments under the key. See [Keyed resolution](../06-resolution/01-resolving-services.md#keyed-resolution).

## Scopes

### IAwaitenScope

Extends `IAwaitenAsyncResolver`, `IDisposable` and `IAsyncDisposable`. A scope owns the scoped instances and transients it builds and disposes them in reverse creation order.

| Member | What it does | See |
|---|---|---|
| `IAwaitenScope CreateScope()` | Opens a child scope that shares the root's singletons. The caller disposes it. | [Scopes](../04-core-concepts/02-scopes.md#open-a-scope) |
| `Task<IAwaitenScope> CreateScopeAsync(ct = default)` | Opens a child scope and initializes its async scoped services in dependency order. If that fails, the scope is disposed and the original exception is thrown. | [Warm a scope](../04-core-concepts/03-async-initialization.md#warm-a-scope) |
| `void Dispose()` | Disposes what the scope owns. Throws when it owns a service that is only `IAsyncDisposable` or has an asynchronous `OnRelease` hook ([AWT156](../09-diagnostics.md#awt156)). | [Disposal](../04-core-concepts/05-disposal.md#dispose-a-scope) |
| `ValueTask DisposeAsync()` | Disposes what the scope owns and awaits asynchronous disposal. Use it through `await using`. | [Async disposal](../04-core-concepts/05-disposal.md#async-disposal) |

Disposing the root also disposes the singletons. Resolving from a disposed scope or root throws `ObjectDisposedException`.

### IAwaitenRoot

The container root. Extends `IAwaitenScope` and `IAwaitenExternalResolverHost`. The generator implements it; consume it, do not implement it, because it may gain members.

| Member | What it does | See |
|---|---|---|
| `Task InitializeAsync(ct = default)` | Builds and initializes every async singleton in dependency order. Idempotent and thread-safe. | [Warm everything up front](../04-core-concepts/03-async-initialization.md#warm-everything-up-front) |
| `AwaitenResolvability GetResolvability(Type serviceType, object? key)` | Answers whether the root resolves the service synchronously, without building anything, and why not when it has the service. | [Feature detection](../08-hosting/01-msdi-bridge.md#feature-detection) |
| `IReadOnlyList<AwaitenRegistration> Registrations` | The public registrations the container resolves, one entry per service type and key. | [Registration metadata](#registration-metadata) |
| `IReadOnlyList<AwaitenExternalDependency> ExternalDependencies` | The services the container expects from its external resolver. Empty when there are none. | [Verifying the wiring](../08-hosting/02-external-services.mdx#verifying-the-wiring) |

### AwaitenResolvability

The answer of `GetResolvability`. The `default` value is `NotRegistered` without a reason.

| Member | What it does |
|---|---|
| `AwaitenResolvabilityStatus Status` | How the root answers the request. |
| `string? Reason` | For `AsyncOnly` and `WithheldOnRoot`, the message `Resolve` would throw, naming the fix. Otherwise `null`. |

| `AwaitenResolvabilityStatus` | Meaning |
|---|---|
| `NotRegistered` (0) | The container has no registration for the type under the key. |
| `Resolvable` (1) | The root resolves it synchronously. |
| `AsyncOnly` (2) | The container has it but serves it only asynchronously, typically because it needs async initialization. |
| `WithheldOnRoot` (3) | The container has it, but the root declines to build it, such as a disposable transient under strict lifetime safety. A child scope resolves it. |
| `External` (4) | The container imports it, so resolving it asks the external resolver. |

New statuses may be added, so give a `switch` over `Status` a default case.

## Owned

### `Owned<T>`

A disposal handle over a service built in a throwaway scope of its own. Resolve or inject `Owned<T>`, `Func<Owned<T>>` or `Func<TArg…, Owned<T>>`, and do not create one with `new` ([AWT205](../09-diagnostics.md#awt205)). See [Owned](../07-lifetime/01-owned.md).

| Member | What it does |
|---|---|
| `T Value` | The service. Valid until the handle is disposed. |
| `void Dispose()` | Disposes the handle's scope and with it everything built for the service. Shared singletons live on. Disposing twice, or disposing a `default` handle, does nothing. |
| `ValueTask DisposeAsync()` | The asynchronous form. Required when the service or anything built for it is only `IAsyncDisposable` or has an asynchronous `OnRelease` hook; a synchronous `Dispose` of such a handle throws. |

## External resolution

These types connect a container to the services the host provides with `[ImportService]` or `[ImportServices]`. See [External services](../08-hosting/02-external-services.mdx).

| Type or member | What it does | See |
|---|---|---|
| `IExternalResolver.TryResolve(Type serviceType, object? key, out object? instance)` | Implement it to hand out host services. `key` carries the `[FromKey]` value, or `null`. Return `false` when the service is unavailable. | [Providing the external resolver](../08-hosting/02-external-services.mdx#providing-the-external-resolver) |
| `IAwaitenExternalResolverHost.ExternalResolver` | The resolver of this scope, or `null`. A child scope without one uses the root's. | |
| `IAwaitenExternalResolverHost.SetExternalResolver(IExternalResolver resolver)` | Sets the resolver once per scope. Setting the same resolver again does nothing; a different one throws `InvalidOperationException`. | [Resolving an imported service directly](../08-hosting/02-external-services.mdx#resolving-an-imported-service-directly) |
| `AwaitenExternalDependency` | One entry of `IAwaitenRoot.ExternalDependencies`: its `ServiceType` and `Key`. | [Verifying the wiring](../08-hosting/02-external-services.mdx#verifying-the-wiring) |

Every generated root and scope implements `IAwaitenExternalResolverHost`. A standalone container usually passes its resolver to the `Root(IExternalResolver?)` constructor instead.

## Registration metadata

`AwaitenRegistration` is one entry of `IAwaitenRoot.Registrations`. The MS.DI bridge projects these entries into a service collection. The implementations of one service type and key coalesce into one entry, the one that wins single resolution.

| Property | What it reports |
|---|---|
| `Type ServiceType` | The service type the container resolves. |
| `Lifetime Lifetime` | The lifetime the container owns it under. |
| `object? Key` | The key the registration declares, or `null`. |
| `bool RequiresAsync` | Whether it has no synchronous path, because it or a dependency needs async initialization or an async factory. The bridge projects it as `Task<T>`. |
| `bool ExternallyOwned` | Whether it is a pre-built `Instance` the container exposes but does not own. |
| `bool DisposalSuppressed` | Whether the container builds a possibly disposable instance with `SuppressDisposal = true` and never disposes it. |

Its constructors and the `AsyncTaskType` and `AsyncTaskConverter` properties serve generated code and the bridge.

## Infrastructure for generated code

These public types exist for the generated code and are hidden from IntelliSense. Do not call or implement them.

- `IAwaitenResolver<T>` and `IAwaitenAsyncResolver<T>`: the typed paths behind `Resolve<T>()` and `ResolveAsync<T>()`.
- The `Awaiten.Internals` namespace, such as `AwaitenOwnedProjection` and `AwaitenTaskProjection`, and the attributes the generator puts on the code of modules.

## MS.DI bridge

The `Awaiten.Extensions.DependencyInjection` package. See [MS.DI bridge](../08-hosting/01-msdi-bridge.md).

### Registering the container

| Member | What it does | See |
|---|---|---|
| `services.AddAwaitenContainer<TRoot>()` | Creates a `TRoot` and projects its registrations into the `IServiceCollection`, with one Awaiten scope per MS.DI scope. An async-only service is projected as `Task<T>`. The root provider disposes the root once the bridge has used it. | [Project into a service collection](../08-hosting/01-msdi-bridge.md#project-into-a-service-collection) |
| `services.AddAwaitenContainer<TRoot>(TRoot root)` | Projects a root you created, for example with your own external resolver. The root stays yours: dispose it after the provider. | [Project into a service collection](../08-hosting/01-msdi-bridge.md#project-into-a-service-collection) |
| `AwaitenContainerBuilder<TRoot>.Services` | The service collection, for chaining further registrations. | |
| `AwaitenContainerBuilder<TRoot>.WithInitialization()` | Adds a hosted service that calls `InitializeAsync` on host startup, wiring the external resolver first if none is set. Calling it again does nothing. | [Warm async services on startup](../08-hosting/01-msdi-bridge.md#warm-async-services-on-startup) |
| `new AwaitenServiceProviderFactory<TRoot>(Action<AwaitenContainerBuilder<TRoot>>? configure = null)` | An `IServiceProviderFactory<TRoot>` for `UseServiceProviderFactory`. It projects the container and builds a standard MS.DI provider, and wires the root to it unless a resolver is set. | [Use it as the host's provider factory](../08-hosting/01-msdi-bridge.md#use-it-as-the-hosts-provider-factory) |

Both `AddAwaitenContainer` overloads and `CreateServiceProvider` throw an `InvalidOperationException` when the container builds a possibly disposable service with `SuppressDisposal = true` ([AWT214](../09-diagnostics.md#awt214)).

### AwaitenServiceProvider

Adapts a root or scope directly to `IServiceProvider`, `IKeyedServiceProvider`, `ISupportRequiredService`, `IServiceScopeFactory`, `IServiceProviderIsService` and `IServiceProviderIsKeyedService`, so Awaiten owns construction and disposal alone. See [Or let Awaiten own everything](../08-hosting/01-msdi-bridge.md#or-let-awaiten-own-everything).

| Member | What it does |
|---|---|
| `new AwaitenServiceProvider(IAwaitenScope container, bool ownsContainer = true)` | Wraps the container. With `ownsContainer`, disposing the provider disposes the container. Wrap the `Root` to get the feature detection and the keyed `Task<T>` projection. |
| `object? GetService(Type serviceType)` | `TryResolve`, so an unregistered service is `null`. A service the container has but withholds throws its guidance instead. |
| `object GetRequiredService(Type serviceType)` | Like `GetService`, but a miss throws the container's own exception naming the cause. |
| `object? GetKeyedService(Type serviceType, object? serviceKey)` and `GetRequiredKeyedService` | The keyed forms. `KeyedService.AnyKey` is declined. |
| `bool IsService(Type serviceType)` and `IsKeyedService(Type serviceType, object? serviceKey)` | Answered from `GetResolvability`, without building anything. |
| `IServiceScope CreateScope()` | Opens a scope backed by an Awaiten child scope. |
| `Dispose()` and `DisposeAsync()` | Dispose the container when the provider owns it. Prefer `await using`. |

### External resolver and verification

| Member | What it does | See |
|---|---|---|
| `new AwaitenServiceProviderExternalResolver(IServiceProvider provider)` | An `IExternalResolver` over an MS.DI provider. A keyed dependency resolves through the provider's `IKeyedServiceProvider`. | [Providing the external resolver](../08-hosting/02-external-services.mdx#providing-the-external-resolver) |
| `provider.VerifyAwaitenContainers()` | Checks every container registered with `AddAwaitenContainer` against the provider and throws an `InvalidOperationException` that lists the missing external dependencies. Returns the provider for chaining. | [Verify external dependencies at startup](../08-hosting/01-msdi-bridge.md#verify-external-dependencies-at-startup) |
| `root.VerifyAgainst(IServiceProvider provider)` | The same check for one container root. | [Verify external dependencies at startup](../08-hosting/01-msdi-bridge.md#verify-external-dependencies-at-startup) |

## Where to go next

- [Attribute reference](./01-attributes.md) for the declarations.
- [Resolving services](../06-resolution/01-resolving-services.md) for the resolve surface with examples.
