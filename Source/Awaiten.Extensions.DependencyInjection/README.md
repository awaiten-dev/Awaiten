# Awaiten.Extensions.DependencyInjection

[![Nuget](https://img.shields.io/nuget/v/Awaiten.Extensions.DependencyInjection)](https://www.nuget.org/packages/Awaiten.Extensions.DependencyInjection)

Awaiten.Extensions.DependencyInjection bridges an [Awaiten](https://www.nuget.org/packages/Awaiten) container into Microsoft.Extensions.DependencyInjection, so ASP.NET Core, the generic host and worker services resolve its services. You keep Awaiten's compile-time checked graph and still fit the host.

## Installation

```shell
dotnet add package Awaiten
dotnet add package Awaiten.Extensions.DependencyInjection
```

Reference `Awaiten` directly as well: it carries the source generator that builds your container.

## At a glance

```csharp
using Awaiten;
using Awaiten.Extensions.DependencyInjection;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddAwaitenContainer<CoffeeShop.Root>()   // project the container into the service collection
                .WithInitialization();                    // warm async services on startup

WebApplication app = builder.Build();
app.Services.VerifyAwaitenContainers();                   // every imported host service is registered

app.MapPost("/orders/{drink}", (string drink, Order order) => order.Add(drink));
app.Run();

[Container(AsyncSafety = AsyncSafety.SyncAfterInit)]
[Singleton<PriceList>]
[Scoped<Order>]                                           // one per request
[ImportService<ILogger<Order>>]                           // provided by the host
public static partial class CoffeeShop;
```

## Three ways to plug in

| API | Pick it when |
|---|---|
| `services.AddAwaitenContainer<TRoot>()` | You add the container to a service collection, such as `builder.Services`, or migrate from MS.DI one group of services at a time |
| `new AwaitenServiceProviderFactory<TRoot>()` | The host builds its provider through a factory, and imported services should resolve from the root before anything else |
| `new AwaitenServiceProvider(root)` | Nothing but the container registers services, so it answers every request itself |

## Features

- **Aligned lifetimes and scopes.** Awaiten lifetimes map to `ServiceLifetime`, and one Awaiten scope aligns to each MS.DI scope, such as an ASP.NET Core request.
- **Host services in the graph.** `[ImportService<T>]` and `[ImportServices]` let the container depend on host registrations, and `VerifyAwaitenContainers()` checks at startup that they exist.
- **Async initialization.** An async-initialized service is projected as `Task<T>`; `WithInitialization()` warms the container on startup, and under `AsyncSafety.SyncAfterInit` consumers take the bare type.
- **Keyed services.** Keyed registrations are projected under their key, so `[FromKeyedServices]` and `GetKeyedService` reach them.
- **Disposal once.** Awaiten disposes everything it built exactly once, with the scope that owns it, including dependencies MS.DI never sees.
- **ASP.NET Core feature detection.** `AwaitenServiceProvider` answers `IServiceProviderIsService`, so minimal APIs and MVC tell injected parameters from request data.
- **Native AOT.** The bridge constructs no generic types at run time.

## Documentation

The full documentation is available at [awaiten.dev](https://awaiten.dev):

- [MS.DI bridge](https://awaiten.dev/docs/hosting/msdi-bridge): the three paths, disposal, startup initialization and verification, and what the bridge does not project
- [External services](https://awaiten.dev/docs/hosting/external-services): importing host services into the container
- [Migrating from MS.DI](https://awaiten.dev/docs/hosting/migrate-from-msdi): moving an existing `IServiceCollection` setup to Awaiten, one group of services at a time
- [Getting started](https://awaiten.dev/docs/getting-started) with Awaiten itself
