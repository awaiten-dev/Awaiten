---
description: Fixes for common Awaiten problems, from a generator that does not run to resolves that throw at run time, each with its cause and the change that resolves it.
toc_max_heading_level: 3
---

# Troubleshooting

Each entry names a symptom, its cause and the fix. Most problems Awaiten reports at build time already; the [Diagnostics](../09-diagnostics.md) page explains every `AWT` id.

## Build

### The generator does not run, or `Root` is not found

**Symptom:** `CoffeeShop.Root` does not compile, for example with the error CS0426 (the type name `Root` does not exist in the type), and the project has no generated file for the container (see [See the generated code](../02-complete-example/01-generated-code.md#see-the-generated-code)).

**Causes and fixes:**

- **The compiler is too old.** The generator needs the compiler of the .NET SDK the [requirements](../01-getting-started.md#requirements) name. An older compiler does not load it and warns instead, typically with CS9057. Install that SDK or a later one, and check `global.json` if the repository pins an older one.
- **The IDE brings its own compiler.** Visual Studio and Rider compile with a compiler of their own. When `dotnet build` succeeds but the IDE shows the error, update the IDE to a version that ships the compiler of that SDK.
- **The language version is too old.** A container needs C# 9 or later, and a module that declares a `[Scan]` needs C# 11. Projects that target .NET Framework or netstandard2.0 default to C# 7.3. Awaiten then reports [AWT243](../09-diagnostics.md#awt243) and generates nothing. Set `<LangVersion>latest</LangVersion>` in the project file.
- **The class is not `static partial`.** A container that is not `static` is [AWT116](../09-diagnostics.md#awt116). One that is not `partial` fails with the compiler error CS0260, because the generated file declares the other part of the class.

## Run time

### Resolve throws "has registration errors; see the build diagnostics"

**Symptom:** Every `Resolve`, `ResolveAsync`, `CreateScope` and `InitializeAsync` on the container throws an `InvalidOperationException` that points at the build diagnostics, and `TryResolve` returns `false`.

**Cause:** The container has an error, such as a missing dependency, that was suppressed or lowered to a warning, so the build passed. A container with an error gets no wiring, only a placeholder `Root` that throws. The exception is an error that a module of the container's own assembly reports about one of its registrations: lowered, or disabled with `#pragma` where it is reported, it only leaves that registration out.

**Fix:** Look for the `AWT` error in the build output and fix its cause instead of suppressing it. See the introduction of [Diagnostics](../09-diagnostics.md) and [A container with errors](../02-complete-example/01-generated-code.md#a-container-with-errors).

### Resolve throws for an async-initialized service

**Symptom:** `Resolve<T>()` throws an `InvalidOperationException` saying the service requires asynchronous initialization, and `TryResolve` returns `false`, although the service is registered.

**Cause:** The service is async-initialized, or it depends on one without deferring, for example through a plain constructor parameter. In the strict default such a service has no synchronous path, so it is never handed out before it is initialized. A typed `Resolve` on the generated `Root` or `Scope` is reported at build time ([AWT219](../09-diagnostics.md#awt219)).

**Fix:** One of these:

- Resolve it with `await shop.ResolveAsync<T>(cancellationToken)`, or inject an awaiting shape such as `Task<T>` or `Func<Task<T>>`. See [Async initialization](../04-core-concepts/03-async-initialization.md#resolve-it-asynchronously).
- When a framework forces a synchronous resolve, set `[Container(AsyncSafety = AsyncSafety.SyncAfterInit)]` and call `InitializeAsync` at startup. A synchronous resolve before warm-up then blocks until the initialization completes. See [Relax the rule](../04-core-concepts/03-async-initialization.md#relax-the-rule-when-you-need-to).

### A disposable transient is withheld on the root

**Symptom:** Resolving a transient that implements `IDisposable` or `IAsyncDisposable` from the `Root` throws an `InvalidOperationException` that points at `Owned<T>`, and `TryResolve` returns `false`. Under the MS.DI bridge, resolving it from the root provider throws the same guidance.

**Cause:** [Strict lifetime safety](../04-core-concepts/04-lifetime-safety.md), the default. The root lives as long as the application, so every disposable transient it built would stay there undisposed until shutdown. A transient with an `OnRelease` hook is withheld the same way.

**Fix:** Resolve it from a scope (`shop.CreateScope()`, or an MS.DI scope), resolve or inject `Owned<T>` or `Func<Owned<T>>` and dispose the handle, or set `LifetimeSafety = LifetimeSafety.Loose` when you need the semantics of Microsoft.Extensions.DependencyInjection. See [Owned](../07-lifetime/01-owned.md).

### A closing of an open generic is "not registered"

**Symptom:** `Resolve<IRepository<Customer>>()` throws "No registration for type" although `[Transient(typeof(Repository<>), typeof(IRepository<>))]` is registered. The build warns with [AWT218](../09-diagnostics.md#awt218) when the call is on the generated `Root` or `Scope`.

**Cause:** Awaiten builds only the closings that the graph asks for, through constructor parameters, factory and hook parameters, injected properties and the shapes over them. A closing you only resolve directly is never generated.

**Fix:** Register the closing explicitly next to the open registration, such as `[Transient<Repository<Customer>, IRepository<Customer>>]`. See [Open generics](../05-registration/05-open-generics.md#resolve-a-closed-type).

### A synchronous Dispose throws

**Symptom:** `Dispose()` on the root, a scope or an `Owned<T>` throws an `InvalidOperationException` about asynchronous disposal.

**Cause:** The container owns a service that implements `IAsyncDisposable` but not `IDisposable`, or one with an asynchronous `OnRelease` hook. A synchronous dispose cannot await them, so it disposes everything else and then throws. Awaiten warns at build time ([AWT156](../09-diagnostics.md#awt156)).

**Fix:** Dispose asynchronously, with `await using` or `await shop.DisposeAsync()`. Under the MS.DI bridge, dispose the provider and its scopes with `DisposeAsync` as well. See [Async disposal](../04-core-concepts/05-disposal.md#async-disposal).

### A single service resolves to an unexpected implementation

**Symptom:** A service with several registrations resolves to another implementation than you expected.

**Cause:** In Awaiten the first registration wins single resolution, not the last one as in Microsoft.Extensions.DependencyInjection. The container's own registrations come first, then the imported modules in the order of their `[Import]`. All registrations stay members of the service's collections. Two imported modules that both register the service without `Fallback` are reported ([AWT155](../09-diagnostics.md#awt155)), since only the import order decides.

**Fix:** Register the implementation you want on the container itself, or make the other registrations overridable defaults with `Fallback`, which a registration of the same service without `Fallback` replaces. When you want every implementation, depend on a collection. See [Overridable defaults](../05-registration/07-modules/index.md#overridable-defaults) and [Collections](../06-resolution/03-collections.md).

### An imported service is missing at run time

**Symptom:** Resolving a service that depends on an `[ImportService]` or `[ImportServices]` type throws, naming the imported service.

**Causes and fixes:**

- **The host does not register it.** Awaiten cannot check the host's registrations at build time. Call `provider.VerifyAwaitenContainers()` at startup, which throws and names every missing external dependency. See [Verify external dependencies at startup](../08-hosting/01-msdi-bridge.md#verify-external-dependencies-at-startup).
- **No external resolver is wired yet.** After a plain `AddAwaitenContainer<TRoot>()`, the root is wired to the host's provider on the first resolution through a projected registration or when `WithInitialization()` starts. Resolving from the root directly before that has no resolver to ask. Use `AwaitenServiceProviderFactory`, which wires the root when it builds the provider, construct the root with its resolver and pass it to `AddAwaitenContainer(root)`, or call `SetExternalResolver` yourself. A standalone container passes its resolver to the `Root` constructor. See [Providing the external resolver](../08-hosting/02-external-services.mdx#providing-the-external-resolver).

### A container in another assembly fails after a library update

**Symptom:** After you update a library whose modules your container imports, the application throws a `TypeLoadException` or `MissingMethodException`, although the library's public API did not change.

**Cause:** A library module generates public factories and handles for the containers of other assemblies. They follow the library's internal types, so they are not a stable binary contract, and a container compiled against an older version of the library can call members that no longer exist.

**Fix:** Rebuild the container against each new version of the library. A package that ships a container importing a module of another package should depend on exactly the version of that package it was built against. See [Shipping modules from a library](../05-registration/07-modules/01-library-modules.md).

### A keyed service is "not registered"

**Symptom:** A keyed resolve throws, or a `[FromKey]` dependency is reported as missing, because the key does not match a registration.

**Cause:** Keys match by type and value, so a typo in a string key, or the string `"Whole"` against a registration keyed with `MilkKind.Whole`, selects nothing.

- At an injection site, Awaiten checks every key: a `[FromKey]` key that nothing is registered under is a missing dependency ([AWT101](../09-diagnostics.md#awt101)).
- A keyed `Resolve`, `TryResolve` or `ResolveAsync` with a constant key on the generated `Root` or `Scope` is checked too ([AWT218](../09-diagnostics.md#awt218)). A call through `IAwaitenRoot`, or with a key only known at run time, is checked at run time only: `Resolve` throws and `TryResolve` returns `false`.

**Fix:** Use the key the registration declares. Prefer `enum` keys, which the C# compiler checks for typos. See [Keyed services](../05-registration/04-keyed-services.md#typed-keys).

## Where to go next

- [Diagnostics](../09-diagnostics.md) for every build-time check with an example and a fix.
- [Runtime API](./02-runtime-api.md) for what each call throws or returns.
