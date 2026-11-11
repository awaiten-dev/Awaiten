---
sidebar_label: Modules in a library
description: Ship a module from a library that keeps its implementations internal, with generated factories, handles and self-compiled scans that let any container import it.
---

# Shipping modules from a library

A library often keeps its implementations `internal` and exposes only interfaces. Its [module](./index.md) registers them like any other type, and a container in another assembly imports the module without the library granting `InternalsVisibleTo`. This page is for library authors: it shows how that works and which rules matter most. The [library module rules](./02-library-module-rules.md) hold the details.

## A minimal example

The library keeps `Kettle` internal and declares its module `public static partial`:

```csharp title="Kettles/KettleModule.cs"
public interface IKettle;
internal sealed class Kettle(ITimeSystem time) : IKettle;   // stays internal

[Module]
[Singleton<Kettle, IKettle>]
// highlight-next-line
public static partial class KettleModule;   // partial, so the generator can add the factory
```

The application imports the module and resolves `IKettle`. It never names `Kettle`:

```csharp title="CoffeeShop/CoffeeShop.cs"
[Container]
[Import(typeof(KettleModule))]
[Singleton<RealTimeSystem, ITimeSystem>]
public static partial class CoffeeShop;
```

The module compiles the registration **in its own build**: it emits a public factory that constructs `Kettle` and returns `IKettle`. The container in the application calls that factory instead of the constructor it cannot reach.

:::warning[Rebuild against each library version]

The members a module generates into `Internals` are public members of the library, but not a stable binary contract. Their signatures follow the internal types, and which of them exist follows the library's internal graph, so renaming an internal service, changing a constructor or taking an internal service in one more place can add, change or remove them. A container compiled against an older version of the library can then fail at run time, for example with a `TypeLoadException` or `MissingMethodException`. A container must be rebuilt against each new version of the library, even when the library's own public API didn't change. A package that ships a container importing a module of another package should depend on exactly the version of that package it was built against. A library built with another Awaiten version than the container is covered in [libraries built by another Awaiten version](./02-library-module-rules.md#libraries-built-by-another-awaiten-version).

:::

## How it works

- For everything a container in another assembly can't reach, the module generates code in its own build: a factory for an internal implementation or one without a public constructor, a wrapper for a non-public member or hook, a handle for a service registered under an internal type, and a factory per match of its `[Scan]`.
- The module keeps these in a nested class named `Internals`, hidden from IntelliSense, so they stay apart from the module's own members. The name is reserved: a module or container that declares a member named `Internals` itself is an error ([AWT235](../../09-diagnostics.md#awt235)).
- A container in another assembly calls the generated code instead of the constructor or member it can't reach, and keeps everything else the registration declares: lifetime, keys, `Fallback`, hooks, async initialization, disposal, collections and decorators.
- A container in the module's own assembly constructs every implementation directly, through the same constructor the factory uses, so moving a module between projects never changes what it registers. Only a type private to the module goes through the module's factory in every assembly.
- A container in an assembly the library grants `InternalsVisibleTo` constructs what it can reach itself.

The [who builds what](./02-library-module-rules.md#who-builds-what) table lists every case.

## Rules

The library's build checks what is wrong for every importer:

- Declare the module `public static partial` and non-generic ([AWT152](../../09-diagnostics.md#awt152), [AWT194](../../09-diagnostics.md#awt194), [AWT201](../../09-diagnostics.md#awt201)). Every class that contains it must be `partial` too.
- The constructor of an implementation another assembly can't construct takes public types, or internal services that a module of the library registers ([AWT195](../../09-diagnostics.md#awt195)), each directly or as one of the relationships a [handle](./02-library-module-rules.md#how-a-dependent-may-take-it) supports ([AWT237](../../09-diagnostics.md#awt237)).
- A hook that runs through a wrapper takes public types or internal services of the library ([AWT203](../../09-diagnostics.md#awt203)), and none of them marked `[FromKey]` or `[Inject]` ([AWT204](../../09-diagnostics.md#awt204)).
- An internal async initializer convention runs through a wrapper, so its method takes public types or internal services of the library ([AWT234](../../09-diagnostics.md#awt234)).
- One implementation is one instance in every container: one registered under several services that no type covers is shared through a handle, and a scan of the module that also matches it is an error ([AWT197](../../09-diagnostics.md#awt197)).
- An `[Inject]` property the generated factory can't set the way a container would is an error ([AWT200](../../09-diagnostics.md#awt200)).
- A scan of the module sweeps the module's own assembly only ([AWT202](../../09-diagnostics.md#awt202)), and a match registers only through interfaces another assembly can name ([AWT196](../../09-diagnostics.md#awt196)).

The consuming container reports what depends on its own graph or on how the library was built, at its `[Import]` or at the dependency:

- a registration, decorator, composite or member the module generated no factory or wrapper for, with the reason the module recorded ([AWT230](../../09-diagnostics.md#awt230), [AWT231](../../09-diagnostics.md#awt231), [AWT108](../../09-diagnostics.md#awt108), [AWT109](../../09-diagnostics.md#awt109), [AWT247](../../09-diagnostics.md#awt247)),
- an internal service only a module it didn't import provides ([AWT238](../../09-diagnostics.md#awt238)),
- a scan match or registration that can't share the instance the module builds ([AWT240](../../09-diagnostics.md#awt240), [AWT241](../../09-diagnostics.md#awt241)),
- a closing over an internal type that nothing can provide ([AWT253](../../09-diagnostics.md#awt253)),
- a collection the module fixed empty although the container registers its elements ([AWT252](../../09-diagnostics.md#awt252)),
- a scan match it can't construct under `SkipUnconstructable` ([AWT141](../../09-diagnostics.md#awt141)),
- a module with no generated code it can read ([AWT154](../../09-diagnostics.md#awt154)).

When the library grants `InternalsVisibleTo`, some of the library's errors become records that only containers without the grant report; see [when the library grants `InternalsVisibleTo`](./02-library-module-rules.md#when-the-library-grants-internalsvisibleto).

## Where to go next

- [Library module rules](./02-library-module-rules.md) for internal implementations, non-public members, services only the module uses and self-compiled scans in detail.
- [Modules](./index.md) for declaring and importing modules, overridable defaults and collections.
- [Scanning](../07-scanning.md) for the scan options a module scan shares with a container scan.
