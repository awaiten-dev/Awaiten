---
description: What Awaiten is, what the compiler checks for you, how it compares with other .NET containers and when another container fits better.
---

# Introduction

Awaiten is a compile-time dependency injection container for .NET. You declare your services with attributes on a container class, and a Roslyn source generator writes plain C# that builds the object graph. Nothing is discovered, reflected over or emitted at run time, so the result is trimming and native AOT clean, and you can read and step through it like your own code.

Awaiten treats asynchronous initialization as part of the graph. A service that needs async setup before it can work, such as an espresso machine that heats its boiler or a payment terminal that connects to the bank, is tracked through the whole graph, including relationships, collections and scopes. The compiler stops any code path that could hand it out before its setup has finished.

## What the compiler checks for you

The generator checks the whole graph while you build and reports each mistake with an `AWT` id, as a build error or a warning. There are more than 100 of them, among others for:

- a dependency that is not registered,
- a dependency cycle,
- a singleton that captures a scoped service,
- an async-initialized service reached through a synchronous path,
- a `Func` on the container root that builds disposable services, whose instances would pile up there for the root's lifetime.

The examples on this page assume the usings and coffee-shop types that the note at the top of [Getting started](./01-getting-started.md) describes.

Forget to register the payment terminal the barista needs, and the build stops instead of the first order:

```csharp expect=AWT101
public sealed class Barista(IPaymentTerminal terminal);

[Container]
// highlight-next-line
[Scoped<Barista>]   // nothing provides an IPaymentTerminal
public static partial class CoffeeShop;
```

```text title="Build error"
error AWT101: 'Barista' cannot be resolved: 'Barista' requires 'IPaymentTerminal', which is not registered on the container
```

Register it, and the build passes:

```csharp
public sealed class Barista(IPaymentTerminal terminal);

[Container]
// highlight-next-line
[Singleton<CardTerminal, IPaymentTerminal>]
[Scoped<Barista>]
public static partial class CoffeeShop;
```

The [Diagnostics](./09-diagnostics.md) page lists every check with an example and a fix.

## How it compares

The columns for other containers describe each category in general. Check a library's own documentation for its specifics.

| | Awaiten | Microsoft.Extensions.DependencyInjection | Reflection-based containers such as Autofac, DryIoc or Simple Injector | Other source-generated containers such as Jab, StrongInject or Pure.DI |
|---|---|---|---|---|
| When the graph is checked | At build time, by the compiler | At run time, optionally when the provider is built | At run time, some with a verification call at startup | At build time |
| Reflection at run time | None | Used to activate services | Used to discover and activate services | None or little |
| Async initialization | Tracked through the graph and checked at build time | Not built in | Not checked at build time | Varies by library |
| Registration style | Attributes on a container class | Calls on an `IServiceCollection` at run time | Registration code at run time | Attributes or setup code that the generator reads |
| Registrations decided by run-time code | Not possible | Possible | Possible | Not possible |
| Native AOT and trimming | Supported | Supported, with limits | Depends on the library | Generally supported |

For resolve and build times against Microsoft's container, Autofac, DryIoc and Simple Injector, see the [Benchmarks](./10-benchmarks.mdx).

## When to pick another container

Awaiten fixes the graph at build time. That is its strength, and it rules out designs that decide the graph at run time. Another container fits better when you need:

- **Registrations that change at run time**, such as replacing or removing a registration, or registering a service only when a setting is on. In Awaiten, a [factory](./05-registration/01-factories-and-instances.md#factory) that takes the setting as a parameter makes such a choice.
- **Plugins loaded at run time**, where assemblies are discovered and their types activated by reflection. Awaiten's [scanning](./05-registration/07-scanning.md) only sees what the compilation references.
- **Lambda factories that capture state.** A factory in Awaiten is a named static method on the container or a module, and the values it needs arrive as parameters.
- **Last registration wins.** Awaiten resolves the first registration, and an override is declared explicitly.
- **A wildcard key** such as `KeyedService.AnyKey`. A key in Awaiten selects exactly one registration.

[Migrating from MS.DI](./07-hosting/03-migrate-from-msdi.md#what-has-no-direct-equivalent) lists these differences with the Awaiten way for each. You do not have to move everything at once: the [MS.DI bridge](./07-hosting/01-msdi-bridge.md) runs a container next to an `IServiceCollection`, so what has no counterpart can stay in MS.DI.

## Frameworks and requirements

- Your project targets anything from .NET Framework 4.8 and netstandard2.0 up to .NET 10. The generated code compiles for each of them.
- You build with the .NET SDK 10.0.300 or later and C# 11 or later, or C# 9 with the `typeof` forms as long as no module declares a `[Scan]`.
- `Awaiten` is the only package you need. Add `Awaiten.Extensions.DependencyInjection` to host a container in ASP.NET Core or the generic host.

[Getting started](./01-getting-started.md#requirements) has the details, including older C# versions and IDE versions.

## Where to go next

- [Getting started](./01-getting-started.md) to build your first container in a few minutes.
- [Complete example](./02-complete-example/index.md) for a realistic container that uses the main features together.
- [Design principles](./03-design-principles.md) for the design Awaiten serves.
- [Migrating from MS.DI](./07-hosting/03-migrate-from-msdi.md) to move an existing `IServiceCollection` over.
