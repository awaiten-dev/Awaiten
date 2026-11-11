---
description: Run an Awaiten container under ASP.NET Core and the generic host through Microsoft.Extensions.DependencyInjection.
---

import PackageInstall from '@site/src/components/PackageInstall';

# MS.DI bridge

Most .NET apps run on Microsoft.Extensions.DependencyInjection. ASP.NET Core, the generic host, and worker services all speak it. The `Awaiten.Extensions.DependencyInjection` package bridges your generated container into that world, so you keep Awaiten's compile-time safety and still fit the host.

Install it alongside the core package:

<PackageInstall package="Awaiten.Extensions.DependencyInjection" />

## Which path?

The bridge offers three ways to hand a container to MS.DI. The first two project the container into a service collection, next to the host's own registrations. The third lets the container answer every request itself.

| | [`AddAwaitenContainer`](#project-into-a-service-collection) | [`AwaitenServiceProviderFactory`](#use-it-as-the-hosts-provider-factory) | [`AwaitenServiceProvider`](#or-let-awaiten-own-everything) |
|---|---|---|---|
| Pick it when | You add the container to a service collection, such as `builder.Services`, or migrate from MS.DI one group at a time | The host builds its provider through a factory, and imported services should resolve from the root before anything else | Nothing but the container registers services |
| Host registrations | Live next to the container's | Live next to the container's | None: the container answers every request |
| Disposes the root | The root provider, once it has used the root; a root you pass in stays yours | The provider | The provider, unless you pass `ownsContainer: false` |
| MS.DI sees per service type and key | One registration: the one that wins single resolution | One registration: the one that wins single resolution | Everything the container resolves |
| An async-initialized service | `Task<T>` in the strict default, the bare type under `SyncAfterInit` | `Task<T>` in the strict default, the bare type under `SyncAfterInit` | `Task<T>` in the strict default, where the bare type throws guidance; both under `SyncAfterInit` |
| Collections, relationships, keyed dictionaries | [Not projected](#what-the-bridge-does-not-project): an MS.DI `IEnumerable<T>` holds the winner and the host's own registrations | [Not projected](#what-the-bridge-does-not-project), as with `AddAwaitenContainer` | Served by the container |

## A complete ASP.NET Core app

The app below projects a container with a singleton price list, an order scoped to each request and a card terminal that connects asynchronously. The order logs through the host's logger, which the container imports. The host warms the terminal on startup and checks that it provides every service the container imports, and a minimal API endpoint takes the projected services as parameters.

```csharp title="Program.cs"
using System.Threading;
using System.Threading.Tasks;
using Awaiten;
using Awaiten.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
// highlight-next-line
builder.Services.AddAwaitenContainer<CoffeeShop.Root>()
                .WithInitialization();

WebApplication app = builder.Build();
app.Services.VerifyAwaitenContainers();

app.MapPost("/orders/{drink}", async (string drink, Order order, IPaymentTerminal terminal) =>
{
    order.Add(drink);
    await terminal.ChargeAsync(order.Total);
    return order.Total;
});

app.Run();

[Container(AsyncSafety = AsyncSafety.SyncAfterInit)]
[AsyncInitializer<IWarmUp>(nameof(IWarmUp.WarmUpAsync))]
[Singleton<PriceList>]
[Scoped<Order>]
[Singleton<CardTerminal, IPaymentTerminal>]
[ImportService<ILogger<Order>>]
public static partial class CoffeeShop;

public interface IWarmUp
{
    Task WarmUpAsync(CancellationToken cancellationToken);
}

public interface IPaymentTerminal
{
    Task ChargeAsync(decimal amount);
}

public sealed class PriceList
{
    public decimal PriceOf(string drink) => drink == "Latte" ? 3.80m : 2.40m;
}

public sealed class Order(PriceList prices, ILogger<Order> logger)
{
    public decimal Total { get; private set; }

    public void Add(string drink)
    {
        Total += prices.PriceOf(drink);
        logger.LogInformation("Added {Drink}", drink);
    }
}

public sealed class CardTerminal : IPaymentTerminal, IWarmUp
{
    public Task WarmUpAsync(CancellationToken cancellationToken) => Task.Delay(100, cancellationToken);   // connect to the reader

    public Task ChargeAsync(decimal amount) => Task.CompletedTask;
}
```

Each request gets its own `Order`, because the request scope of ASP.NET Core aligns to an Awaiten scope. `AsyncSafety.SyncAfterInit` lets the endpoint take the warmed `IPaymentTerminal` directly; in the strict default, MS.DI sees it as `Task<IPaymentTerminal>` instead. The sections below explain each call.

## Project into a service collection

`AddAwaitenContainer<TRoot>` registers the container and projects its registrations into the `IServiceCollection`. Awaiten lifetimes align to `ServiceLifetime`, and one Awaiten scope aligns to each MS.DI scope.

```csharp
using Awaiten.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

ServiceCollection services = new();
services.AddAwaitenContainer<CoffeeShop.Root>();

await using ServiceProvider provider = services.BuildServiceProvider();
PriceList prices = provider.GetRequiredService<PriceList>();
```

It returns an `AwaitenContainerBuilder<TRoot>` for the container's hosting options, such as [startup initialization](#warm-async-services-on-startup). Its `Services` property is the collection, for chaining further registrations.

To configure the root before the host sees it, for example to construct it with your own external resolver, build it yourself and pass it in. The root is projected as-is and stays yours, as with any instance you register in MS.DI: the provider never disposes it, so dispose it after the provider.

```csharp
await using CoffeeShop.Root shop = new(myResolver);
services.AddAwaitenContainer(shop);
```

In the strict default, an async-only service has no synchronous path, so this projection does not register its bare type. It is available as `Task<T>`, which routes through `ResolveAsync`. Under [`AsyncSafety.SyncAfterInit`](#warm-async-services-on-startup) the projection registers the bare type instead, and no `Task<T>`.

```csharp
Task<EspressoMachine> machine = provider.GetRequiredService<Task<EspressoMachine>>();
```

A keyed registration is projected under its key, so `[FromKeyedServices]` and `GetKeyedService` reach it. In the strict default, an async-only keyed service is projected as a keyed `Task<T>`, the same way its unkeyed counterpart is. That keyed `Task<T>` is a feature of the bridge: the container's own keyed resolve serves plain services only, so the bridge builds the task through the keyed `ResolveAsync`.

```csharp
public sealed class LatteBar([FromKeyedServices("Oat")] IMilk milk);
```

A [decorated import](./02-external-services.mdx#decorate-an-imported-service) is not projected, since the decorator wraps the host's own registration and would otherwise replace it. A host consumer that resolves the service from MS.DI gets the host's instance, and the services the container builds get the decorator.

## Use it as the host's provider factory

For the generic host or `WebApplicationBuilder`, plug in `AwaitenServiceProviderFactory<TRoot>`. Host registrations and Awaiten services coexist, so a host component can inject an Awaiten service and the other way round. The factory wires the root to the host's provider as soon as it builds it, unless you set a resolver yourself, so the root resolves its [imported services](./02-external-services.mdx) from the start.

```csharp
builder.Host.UseServiceProviderFactory(
    new AwaitenServiceProviderFactory<CoffeeShop.Root>());
```

The factory takes an optional callback that receives the same `AwaitenContainerBuilder<TRoot>`, so its options read the same on both paths:

```csharp
builder.Host.UseServiceProviderFactory(
    new AwaitenServiceProviderFactory<CoffeeShop.Root>(c => c.WithInitialization()));
```

## Disposal

On both paths above, Awaiten disposes everything it built exactly once, with the scope that owns it. That includes disposables reached only as nested dependencies, which MS.DI never sees, and instances awaited through `Task<T>`.

- The Awaiten scope aligned to an MS.DI scope is disposed with that scope.
- The container root is disposed with the root provider. After a plain `AddAwaitenContainer<TRoot>()`, the root provider takes over the root on the first bridged resolution or when `WithInitialization()` warms it, so dispose a root you only use directly yourself.
- A root you passed to `AddAwaitenContainer(root)` is always yours to dispose.
- A pre-built `Instance` member stays yours and is never disposed.
- Do not dispose a scope or the provider while a `Task<T>` resolved from it is still pending: MS.DI disposes that task too, and disposing an incomplete task throws.

Disposing the provider or a scope with `DisposeAsync` drains them asynchronously, so `IAsyncDisposable`-only services are supported there.

MS.DI additionally calls `Dispose` on every instance a projected registration hands out, once per service type it was resolved as, before the Awaiten scope or root that built it. That is how MS.DI treats any factory registration, for example `AddSingleton<IWriter>(sp => sp.GetRequiredService<Ledger>())`, and it relies on `Dispose` being idempotent, as the `IDisposable` contract requires. A singleton `Ledger` resolved as both `IReader` and `IWriter` therefore sees three `Dispose` calls: one per service type from MS.DI, then one from Awaiten.

That is also why a service built with `SuppressDisposal = true` cannot be projected when it is or may be disposable: MS.DI would dispose it no matter what the container promised. Both paths above throw at startup instead, naming the service. [AWT214](../09-diagnostics.md#awt214) reports the projection at build time when the container is declared in the same project and the call names its `Root` type; elsewhere the startup throw catches it. Remove `SuppressDisposal`, register a pre-built `Instance` you own, or let Awaiten own everything as described next. In the strict default, a service that needs asynchronous initialization is unaffected, because MS.DI only receives its `Task<T>`. Under `SyncAfterInit` its bare type is projected, so the rule applies to it too.

## Or let Awaiten own everything

`AwaitenServiceProvider` adapts an Awaiten scope directly to `IServiceProvider`, `IKeyedServiceProvider`, and `IServiceScopeFactory`. Awaiten is then the single owner of construction and disposal. `GetService` maps to `TryResolve`, so a missing service returns `null`, and `GetKeyedService`/`GetRequiredKeyedService` reach a keyed registration by its key. `GetRequiredService` and `GetRequiredKeyedService` throw the container's own `InvalidOperationException` for a missing service, which names the cause, for example an imported service the host does not provide, rather than a generic "no service" message.

```csharp
await using CoffeeShop.Root shop = new();
await using AwaitenServiceProvider provider = new(shop);

using IServiceScope scope = provider.CreateScope();   // the provider is its own IServiceScopeFactory
```

Pass `ownsContainer: false` if you want to keep ownership of the container yourself.

Here the container answers every request, so MS.DI consumers get everything it resolves: the synthesized [collections](../06-resolution/03-collections.md), the [relationship shapes](../06-resolution/02-relationships.md) and the [keyed dictionaries](../06-resolution/04-keyed-dictionaries.md) included. In the strict default, the container serves an async-only service as `Task<T>`, and asking for its bare type throws the container's guidance, which points to `ResolveAsync` or `AsyncSafety.SyncAfterInit`, rather than returning `null`.

### Feature detection

The provider also answers `IServiceProviderIsService` and `IServiceProviderIsKeyedService`, from the container's registration metadata and without constructing anything. ASP.NET Core depends on this: minimal APIs ask it whether a handler parameter comes from dependency injection or from the request, and MVC asks it to infer which action parameters bind from services.

```csharp
IServiceProviderIsService probe = provider.GetRequiredService<IServiceProviderIsService>();
probe.IsService(typeof(IBrewer));                 // true
probe.IsService(typeof(EspressoMachine));         // true even when async-only: resolving it then throws guidance naming ResolveAsync
```

What the probe reports:

- **Everything the container dispatches**, not merely what it advertises as a registration: a registration, the [relationship shapes](../06-resolution/02-relationships.md) over it, the synthesized [collections](../06-resolution/03-collections.md) and [keyed dictionaries](../06-resolution/04-keyed-dictionaries.md), the awaited views over those, and a [variance](../06-resolution/08-generic-variance.md)-compatible closing of a registered variant generic interface. The answer comes from the container itself, through `IAwaitenRoot.GetResolvability`.
- **Existence, not buildability**, which is MS.DI's own semantics. A disposable transient asked on the root is reported, and so is an async-initialized service asked synchronously, as `AsyncOnly`. Resolving either throws the container's guidance naming the fix rather than returning `null`, so the failure lands at the cause instead of letting a host bind the parameter from somewhere else.
- **Keyed async-only services.** The container serves `Task<T>` of an unkeyed async-only service itself, but no relationship shape under a key, so for a keyed async-only service the bridge adds its own keyed `Task<T>` projection, which `IsKeyedService` reports and `GetKeyedService` resolves.
- **Imported services** are the host's to answer, because `GetService` asks the host for them. When the container's external resolver is an `AwaitenServiceProviderExternalResolver`, the probe asks that provider's own `IServiceProviderIsService`, or `IServiceProviderIsKeyedService` under a key. So `[ImportServices]` reports only the types the host has, not every type. Any other resolver, or a host provider without the probe, reports `false`.
- **No probe without metadata.** Wrapping a bare scope rather than a `Root` leaves the provider without metadata, and it then does not offer the probe at all, so the framework keeps its own fallback.

<details>
<summary>Tell a withheld service from a missing one</summary>

The same answer is available to your own code through `IAwaitenRoot.GetResolvability`. It tells a failed `TryResolve` apart: one service is genuinely not the container's, another is a mistake with a named fix. An adapter that must answer an unknown type with `null`, because that is how it says "not mine" to a framework, wants this before it decides between returning `null` and throwing.

Its `Status` is `NotRegistered`, `Resolvable`, `AsyncOnly`, `WithheldOnRoot` or `External`, and for `AsyncOnly` and `WithheldOnRoot` its `Reason` is the message `Resolve` would throw. The [Runtime API](../12-reference/02-runtime-api.md#awaitenresolvability) describes each status.

```csharp
AwaitenResolvability answer = shop.GetResolvability<EspressoMachine>();
if (answer.Reason is { } reason)
{
    throw new InvalidOperationException(reason);   // AsyncOnly or WithheldOnRoot: the fix is named
}
```

- `NotRegistered` also covers shapes of a service the container has but does not build by type. Without a key, `Resolve` throws a message that names the shape to ask for instead:
  - a service with [runtime arguments](../06-resolution/06-runtime-arguments.md), and its `Func<T>`, `Lazy<T>`, `Owned<T>` and `Task<T>`, because only its `Func<TArg…, T>` builds it;
  - a `Task<T>` over a service that needs neither asynchronous initialization nor runtime arguments;
  - a collection of relationships, such as `IEnumerable<Lazy<T>>`.
- Under a non-null key it covers the keyed surface exactly as the keyed `Resolve` does, which serves plain services only. So a relationship or collection under a key is `NotRegistered` there, and so is a keyed `Task<T>`, even though the bridge projects one for an async-only keyed service, and so is a keyed service with runtime arguments or one built by a `[RequestingType]` factory. `GetResolvability(Type)` and `GetResolvability<T>()` ask without a key, matching `Resolve(Type)` and `Resolve<T>()`.
- It lives on the root because that is where the answer belongs: a child scope builds a root-withheld transient normally.

</details>

<details>
<summary>Collections and the limits of the probe</summary>

`IEnumerable<T>` resolves for every `T`, empty when the container has no *unkeyed* registration of the element type. MS.DI consumers rely on that guarantee: a collection is a framework's usual extension point, and "every registered handler, of which there may be none" has to answer rather than hand back `null`.

- A service type registered only under keys counts as empty here, as in MS.DI, where keyed registrations do not join an unkeyed `IEnumerable<T>`.
- The guarantee covers `IEnumerable<T>` alone, again as in MS.DI, which answers `null` for `T[]`, `IList<T>` and `IReadOnlyList<T>` of an unregistered element type.
- A collection of relationships such as `IEnumerable<Lazy<T>>` is empty for the same reason: its element type is `Lazy<T>`, which is not registered. Inside the container's own graph that dependency is an error ([AWT254](../09-diagnostics.md#awt254)).
- Where the container has unkeyed members and cannot produce the shape for them, such as a collection with an async-initialized member, resolution throws the container's guidance rather than returning an empty sequence that would silently drop them.

Two answers under-report, and both want a `[FromServices]` on the parameter. In both, the probe is faithful to what the bridge will do:

- **A closing only the framework asks for.** An [open generic](../05-registration/06-open-generics.md) registration is expanded per closing that something in the container's own graph asks for. With `[Singleton(typeof(Repo<>), typeof(IRepo<>))]` and nothing in the graph consuming `IRepo<Order>`, `IsService(typeof(IRepo<Order>))` is `false` where MS.DI answers `true`.
- **`IEnumerable<T>` of a value type the graph never mentioned.** This is a native-AOT constraint: the empty sequence needs the `T[]` type, which AOT generates on demand for a reference element type but not for a value one.

If you write an adapter for a framework whose dependency-injection surface is not MS.DI, honor the same convention, because it is the framework's expectation rather than MS.DI's implementation detail. A resolver that answers `null` for an unresolvable `IEnumerable<T>` breaks any framework that uses a collection as an extension point, and the failure names the collection rather than the cause. Answer an empty array of the element type instead, and only for a reference element type, for the AOT reason above.

</details>

## Warm async services on startup

`WithInitialization()` registers an `IHostedService` that calls `InitializeAsync` on startup. It wires the external resolver first unless you set one, so `[ImportService<T>]` dependencies resolve during warm-up. It is idempotent.

```csharp
services.AddAwaitenContainer<CoffeeShop.Root>()
        .WithInitialization();
```

Pair this with `[Container(AsyncSafety = AsyncSafety.SyncAfterInit)]` so warmed singletons resolve synchronously afterwards. The projection then registers each async-initialized service under its bare type instead of `Task<T>`, so MS.DI consumers take it directly, as in the [complete app](#a-complete-aspnet-core-app). A consumer resolved before the warm-up finishes still gets an initialized instance, but its resolve blocks the calling thread until the initialization completes.

## Verify external dependencies at startup

An Awaiten graph can depend on host services through `[ImportService<T>]` and `[ImportServices]`. See [External services](./02-external-services.mdx). Verify they are actually registered before you serve traffic.

```csharp
provider.VerifyAwaitenContainers();
```

If a dependency is missing, verification throws and names it, including keyed ones. This is the runtime version of the compile-time missing-dependency check, across the host boundary.

## Native AOT

The bridge constructs no generic types at run time. The generator emits the closed `Task<T>` type and a typed converter into the registration metadata, so the bridge never calls `MakeGenericType` or `MakeGenericMethod`. The one type `AwaitenServiceProvider` creates at run time is the empty array behind an `IEnumerable<T>` of an unregistered reference element type, which native AOT supports. Every CI build publishes an app that uses the container and the bridge with native AOT and runs it.

## What the bridge does not project

`AddAwaitenContainer` and `AwaitenServiceProviderFactory` project one registration per service type and key, the one that wins single resolution, and the container's external dependencies. They do not project:

- **Collections.** An MS.DI `IEnumerable<ISyrup>` holds only the projected winner, plus any `ISyrup` the host registers itself, however many syrups the container registers.
- **Relationship shapes** such as `Func<T>`, `Lazy<T>` and `Owned<T>`, which the container synthesizes for its own graph.
- **Keyed dictionaries** such as `IReadOnlyDictionary<string, IMilk>`.
- **Services with [runtime arguments](../06-resolution/06-runtime-arguments.md)**, which only their `Func<TArg…, T>` builds.
- **A [decorated import](./02-external-services.mdx#decorate-an-imported-service)**, whose host registration stays as it is.

For an MS.DI consumer that needs one of these shapes, register a container service that holds it, and inject that service instead:

```csharp
public sealed class SyrupShelf(IEnumerable<ISyrup> syrups)
{
    public IReadOnlyList<ISyrup> All { get; } = syrups.ToList();
}
```

With `[Singleton<SyrupShelf>]` on the container, an MS.DI consumer takes `SyrupShelf` and sees every syrup. Under [`AwaitenServiceProvider`](#or-let-awaiten-own-everything) none of this applies, because the container answers every request.

## Where to go next

- [Async initialization](../04-core-concepts/03-async-initialization.md) for the initialization the hosted service drives.
- [External services](./02-external-services.mdx) for `[ImportService<T>]` and `[ImportServices]`.
- [Migrating from MS.DI](./03-migrate-from-msdi.md) to move an existing `IServiceCollection` setup to Awaiten, one group of services at a time.
