---
description: Install Awaiten, build your first container, add a service that warms up asynchronously and let the compiler check your wiring.
---

import PackageInstall from '@site/src/components/PackageInstall';

# Getting started

Awaiten is the async-first dependency injection container for .NET. It wires your object graph at build time and checks it with the compiler, including services that need asynchronous setup.

This page opens a coffee shop from scratch. By the end you have a working container, you have added an espresso machine that warms up before it serves, and you have seen the compiler catch a mistake.

:::note[About the examples]
A code block in these docs assumes `using Awaiten;`, the implicit usings of an SDK-style project and the coffee-shop types such as `EspressoMachine`, `Order` and `Cup`, unless it is a complete program titled `Program.cs`.
:::

## Requirements

- The .NET SDK 10.0.300 or later, for the C# compiler the source generator needs. Visual Studio and Rider bring a compiler of their own, so keep the IDE at a version that ships the compiler of that SDK, or the generator does not run there. See [Troubleshooting](./12-reference/04-troubleshooting.md).
- C# 11 or later for Awaiten itself, or C# 9 with the `typeof` forms described below. The examples in these docs use C# 12 features such as primary constructors, classes without a body and collection expressions, so keep the language version at `latest`. Projects that target .NET Framework or netstandard2.0 default to C# 7.3, so set `<LangVersion>latest</LangVersion>` in the project file.
- Any target framework from .NET Framework 4.8 and netstandard2.0 up to `net10.0`.

<details>
<summary>Older C# versions and polyfills</summary>

C# 9 and 10 work too, as long as no module of the project declares a `[Scan]`: use the `typeof` forms, such as `[Singleton(typeof(Espresso))]`, which every generic attribute except `[AsyncInitializer<T>]` also has. On an older version Awaiten reports [AWT243](./09-diagnostics.md#awt243) instead of generating code.

On .NET Framework and netstandard2.0, which lack the `NotNullWhen` attribute, the generated code still needs no polyfill package. Without one, only code in the same project from C# 11 learns that the `out` instance of `TryResolve` is not null when it returns `true`. For that annotation in other assemblies or below C# 11, add a polyfill such as the `Nullable` package or PolySharp.

</details>

## Install the package

<PackageInstall package="Awaiten" />

`Awaiten` is all you need: the attributes, the source generator and the runtime types. It has no third-party dependencies; on netstandard2.0 it references only `Microsoft.Bcl.AsyncInterfaces`, for async disposal. When you host the container in ASP.NET Core or the generic host, add `Awaiten.Extensions.DependencyInjection` as well, as the [MS.DI bridge](./07-hosting/01-msdi-bridge.md) describes.

## Build your first container

A container is a `static partial` class marked with `[Container]`. You register services on it with lifetime attributes, and the source generator writes the wiring at build time.

```csharp title="Program.cs"
using Awaiten;

await using CoffeeShop.Root shop = new();

Cup cup = shop.Resolve<Cup>();
Console.WriteLine($"Your cup holds {cup.Contents}.");

public sealed class Grinder
{
    public string Grind() => "fresh grounds";
}

public sealed class Cup(Grinder grinder)
{
    public string Contents => grinder.Grind();
}

[Container]
[Singleton<Grinder>]
[Transient<Cup>]
public static partial class CoffeeShop;
```

```text title="Output"
Your cup holds fresh grounds.
```

The generator emits a nested `Root` type. The `[Container]` is your [composition root](./03-design-principles.md#the-composition-root), and the generated `Root` is its runtime instance: it owns the singletons and it is the outermost scope. You create it, then ask it for services.

Awaiten built the `Cup` and passed in the shared `Grinder`. You wrote no factory code. A singleton is created once and shared, a transient is made fresh for every resolve.

Dispose the `Root` when you are done. It disposes everything it owns. Use `await using`, so services that dispose asynchronously are torn down correctly too.

## Add a service that warms up

Some services need asynchronous setup before they can work. The espresso machine has to heat its boiler before it pulls a shot. Add it to the shop and tell the container how it warms up:

```csharp title="Program.cs"
using Awaiten;

await using CoffeeShop.Root shop = new();

// highlight-next-line
Cup cup = await shop.ResolveAsync<Cup>();   // the espresso machine warms up first
Console.WriteLine($"Your cup holds {cup.Contents}.");

public interface IWarmUp
{
    Task WarmUpAsync(CancellationToken cancellationToken);
}

public sealed class EspressoMachine : IWarmUp
{
    public Task WarmUpAsync(CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);   // stands in for heating the boiler

    public string PullShot() => "an espresso shot";
}

public sealed class Cup(EspressoMachine machine, Grinder grinder)
{
    public string Contents => $"{machine.PullShot()} from {grinder.Grind()}";
}

[Container]
// highlight-start
[AsyncInitializer<IWarmUp>(nameof(IWarmUp.WarmUpAsync))]
[Singleton<EspressoMachine>]
// highlight-end
[Singleton<Grinder>]
[Transient<Cup>]
public static partial class CoffeeShop;
```

```text title="Output"
Your cup holds an espresso shot from fresh grounds.
```

`[AsyncInitializer<IWarmUp>]` tells the container that every `IWarmUp` is warmed up by `WarmUpAsync`. The container awaits it once, before anything receives the machine. `IWarmUp` is your own interface, so the machine never references Awaiten.

The `Cup` needs the machine, so it is asynchronous too: resolve it with `ResolveAsync<T>()`. Services without asynchronous setup still resolve synchronously with `Resolve<T>()`.

### Warm up when the shop opens

In an app you usually warm everything up at startup, so no request waits for the boiler. `InitializeAsync` initializes every async singleton ahead of time, in dependency order:

```csharp
await using CoffeeShop.Root shop = new();
await shop.InitializeAsync();   // the machine warms up here, once

Cup cup = await shop.ResolveAsync<Cup>();   // ready immediately
```

Under ASP.NET Core and the generic host, the [MS.DI bridge](./07-hosting/01-msdi-bridge.md#warm-async-services-on-startup) does this for you during host startup when you add `WithInitialization()`.

## Register against an interface

Most services hide behind an interface. Pass the implementation first, then the service type:

```csharp
await using CoffeeShop.Root shop = new();
IBrewer brewer = shop.Resolve<IBrewer>();   // the container hands out the EspressoBrewer

public interface IBrewer;
public sealed class EspressoBrewer : IBrewer;

[Container]
[AsyncInitializer<IWarmUp>(nameof(IWarmUp.WarmUpAsync))]
[Singleton<EspressoMachine>]
[Singleton<Grinder>]
// highlight-next-line
[Singleton<EspressoBrewer, IBrewer>]
[Transient<Cup>]
public static partial class CoffeeShop;
```

## Open a scope per order

Some things belong to a single order. Register them as `[Scoped]` and open a scope for each order:

```csharp
await using CoffeeShop.Root shop = new();

await using (IAwaitenScope order = shop.CreateScope())
{
    Order current = order.Resolve<Order>();
    current.Items.Add("an espresso");
} // the order and everything it owns is disposed here

public sealed class Order
{
    public List<string> Items { get; } = [];
}

[Container]
[AsyncInitializer<IWarmUp>(nameof(IWarmUp.WarmUpAsync))]
[Singleton<EspressoMachine>]
[Singleton<Grinder>]
[Singleton<EspressoBrewer, IBrewer>]
// highlight-next-line
[Scoped<Order>]
[Transient<Cup>]
public static partial class CoffeeShop;
```

Inside one scope you get the same `Order` every time. A different scope gets a different one. See [Scopes](./04-core-concepts/02-scopes.md) for the full picture.

## Let the compiler check your wiring

Mistakes in the graph do not wait for run time. Let a cup take the espresso machine through a synchronous `Func<T>`, so it could pull a shot before the boiler is hot, and the build stops:

```csharp expect=AWT119
// highlight-next-line
public sealed class Cup(Func<EspressoMachine> machine);   // the machine may still be cold

[Container]
[AsyncInitializer<IWarmUp>(nameof(IWarmUp.WarmUpAsync))]
[Singleton<EspressoMachine>]
[Transient<Cup>]
public static partial class CoffeeShop;
```

```text title="Build error"
error AWT119: 'Cup' resolves 'EspressoMachine' through a synchronous Func<>, but 'EspressoMachine' is async-initialized; resolve it through ResolveAsync, or set AsyncSafety.SyncAfterInit on the [Container]
```

Take `Func<Task<EspressoMachine>>` instead and await it, and the cup only ever gets a warm machine.

A missing registration, a dependency cycle and a singleton that captures a scoped service are caught the same way. Awaiten ships more than 100 diagnostics; the [Diagnostics](./09-diagnostics.md) page lists them all with an example and a fix.

## Where to go next

- [Complete example](./02-complete-example/index.md) for a full container that uses the main features together.
- [Lifetimes](./04-core-concepts/01-lifetimes.md) and the other core concepts, for singleton, scoped and transient in depth.
- [MS.DI bridge](./07-hosting/01-msdi-bridge.md) to run a container under ASP.NET Core and the generic host.
- [Migrating from MS.DI](./07-hosting/03-migrate-from-msdi.md) to map your `IServiceCollection` registrations to Awaiten.
- [Reference](./12-reference/index.md) to look up an attribute, a runtime method, a term or a fix.
