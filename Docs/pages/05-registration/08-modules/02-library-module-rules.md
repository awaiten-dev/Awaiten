---
sidebar_label: Library module rules
description: The detailed rules for a module that a container in another assembly imports, from generated factories, wrappers and handles to self-compiled scans and InternalsVisibleTo.
---

# Library module rules

This page is the reference for a [module shipped from a library](./01-library-modules.md): what the module generates for each kind of registration, what a container in another assembly does with it, and which build reports what. Read the [guide](./01-library-modules.md) first.

## Who builds what

What a container builds itself and what it leaves to the module depends on where the container lives:

| What the module registers | Container in the library's own assembly | Container in an assembly the library grants `InternalsVisibleTo` | Container in any other assembly |
|---|---|---|---|
| A public implementation with a public constructor | constructs it | constructs it | constructs it, but calls the module's [generated factory](#internal-implementations) when the type has an `[Inject]` property this assembly can't set, and builds it through a [generated handle](#services-only-the-module-uses) when the module also registers it under an internal service |
| An implementation that is internal or has no public constructor | constructs it | constructs it, like any type it can reach | calls the module's [generated factory](#internal-implementations) |
| A type private to the module, or to a class that contains it | calls the module's factory, the internal one that takes internal services directly when there is one | calls the module's factory, the internal one that takes internal services directly when there is one | calls the module's factory, or builds it through a [generated handle](#services-only-the-module-uses) when it is registered under an internal service |
| A service registered under an internal type | constructs it, and calls the module's internal wrapper for a private `Factory` or `Instance` member and the module's factory for a type private to the module | the same, and registers no handles | builds it through a [generated handle](#services-only-the-module-uses) |
| A match of the module's `[Scan]` | expands the scan and constructs the match, except a match private to the module, which it builds through the module's factory | calls the module's factory per match, the internal one that takes internal services directly when there is one, and constructs a match the module recorded instead of generating a factory for it | calls the module's factory per match ([self-compiled scans](#self-compiled-scans)) |

## Internal implementations

When another assembly cannot construct a registered implementation, because the type or every constructor of it is not public, the module compiles the registration in its own build: it emits a public factory that constructs the implementation and returns the registered service, or the implementation itself when it is public. The same holds for a public implementation with an `[Inject]` property that another assembly cannot set, because the property or its setter is `internal`.

A container in another assembly calls that factory instead of the constructor it cannot reach, and keeps everything else the registration declares:

- the lifetime, `Eager`, `Key`, `WhenInjectedInto`, `Fallback` and `SuppressDisposal`,
- its place in collections,
- the decorators the container adds.

It reads from the implementation's metadata which of the module's [async initializer conventions](../../04-core-concepts/03-async-initialization.md#which-conventions-apply) it matches and whether it is disposable, so it initializes, disposes and shares the implementation exactly as one it constructs itself.

- When the container, another module or the base of an [extending container](../09-extending-containers.md) also registers a public implementation that the module builds this way, for example `[Singleton<Greeter>]` beside the module's `[Singleton<Greeter, IGreeter>]`, that registration calls the module's factory too, so both share one instance, as in the library's own assembly. Its own `OnActivated` or `OnRelease` hook is still the method of the container, module or base that declares the registration, and a hook that differs from the module's conflicts as in the library's own assembly ([AWT166](../../09-diagnostics.md#awt166)).
- This includes a module of the container's assembly or of a third library that registers the implementation without being able to construct it: since a module of the implementation's assembly builds it, such a module generates no factory for it and reports nothing in its own build, and a container that imports it without that module reports it ([AWT104](../../09-diagnostics.md#awt104), or [AWT231](../../09-diagnostics.md#awt231) for a module of another assembly). When no module of the implementation's assembly builds it, the module reports AWT104 in its own build. The first of these registrations decides which [async initializer conventions](../../04-core-concepts/03-async-initialization.md#which-conventions-apply) initialize the one instance: those of the container, module or base that declares it.
- An `[Arg]` or `[FromKey]` on a constructor parameter is copied onto the factory's parameter, so a container in another assembly passes the runtime argument or resolves the keyed service just as the constructor asks.
- An `OnActivated` or `OnRelease` hook may take the internal type: the module emits a public wrapper for it.
- A module or service another assembly cannot name never reaches such a container, so the module self-compiles nothing for it. A type private to the module is the exception: the module generates its factory whatever its own accessibility, because nothing else can construct the type.

### Injected properties

- An `[Inject]` property becomes a factory parameter with the property's name, and the factory assigns it, an internal one too, which a container in another assembly can't even see. Such a container resolves the parameter like the property, so `[ImportServices]` doesn't take a missing service from the host for it ([AWT101](../../09-diagnostics.md#awt101)).
- A property marked `Optional` becomes an optional parameter with a presence flag: a container that registers the service sets the flag, so the factory assigns the service even when it resolves to `null`, and a container that doesn't register it leaves the property as it is. This works for a value type such as `Owned<T>` and for up to four init-only properties of one type too, since the factory needs an object initializer for each combination of them that is present.
- A property marked `Deferred` gets a generated setter, which a container calls after it built the instance, so it can still break a cycle.
- A property marked both is assigned through the setter when the service is registered.
- What no container supports is rejected in the library's build as well ([AWT200](../../09-diagnostics.md#awt200)):
  - a `Deferred` property that is init-only or required,
  - an `Optional` one that is required and not a collection,
  - a property without a setter the module can reach.

### Container-side property entries

A container in another assembly may declare an [`[InjectProperty]`](../../06-resolution/05-property-injection.md) entry for a type the module builds:

- For a type the module builds through its generated factory, the container sets the property on the instance the factory returns, before anything else sees it. The property needs a `set` accessor: an init-only one is [AWT178](../../09-diagnostics.md#awt178).
- For a type the module builds into a [generated instance handle](#handles), also when another registration or a scan shares the handle's instance, the container sets the property on the instance the handle holds, before the module's hooks and async initializers run. The entry is [AWT178](../../09-diagnostics.md#awt178) when it is `Deferred`, or when the module builds the type from runtime `[Arg]` arguments.
- A composite the module builds through its factory is [AWT178](../../09-diagnostics.md#awt178), because that factory returns the service rather than the composite. So is a type a `Factory` or `Instance` member of the module produces, in every assembly.
- A container in an assembly the library grants `InternalsVisibleTo` constructs such a type itself and fills every entry.

### The factory's return type

The factory returns one type, and every service of the implementation must convert from it, so that one instance serves them all:

1. the implementation, when it's public,
2. otherwise a registered service that covers the others,
3. or else a public base class or interface of the implementation that covers them.

An internal implementation registered as `IGreeter` and as `IFormalGreeter : IGreeter` gets a factory that returns `IFormalGreeter`. Without a covering type, the module shares the one instance through a generated handle instead (see [services only the module uses](#services-only-the-module-uses)). When one of the module's scans also matches the implementation, it's rejected ([AWT197](../../09-diagnostics.md#awt197)), because a second factory would split the instance a container shares.

### Private types in the module

The implementation can also be a private type nested in the module, or in a class that contains the module. The module can construct such a type, but other code in the assembly can't, so every container calls the module's factory, even a container in the same assembly. Its hooks can be private too.

```csharp
public interface IKettle;

[Module]
[Singleton<Kettle, IKettle>(OnActivated = nameof(Preheat))]
public static partial class TeaModule
{
    private sealed class Kettle(ITimeSystem time) : IKettle;

    private static void Preheat(Kettle kettle) { }
}
```

- When it's registered under an internal service, its factory and hook wrappers are internal, and a container in another assembly builds it through a [generated handle](#services-only-the-module-uses) instead.
- The module always generates this factory, so it must be `partial`, even when it is `internal`. Every class that contains the module must be `partial` as well ([AWT194](../../09-diagnostics.md#awt194)).
- The factory of an `internal` module may take internal parameter types, because only the module's assembly and the assemblies it grants `InternalsVisibleTo` can call it. The same holds for the parameters of its hooks and for its conventions.

### Decorators and composites

- A decorator or composite another assembly cannot construct gets a factory too. It mirrors the constructor, the inner service of a decorator and the collection of a composite included, and returns the service. So a container in another assembly keeps the decorator's place in the chain and passes the composite the other registrations of the service.
- A decorator or composite private to the module, or to a class that contains it, is built through this factory by every container.
- A container in another assembly reports a decorator or composite whose factory the module can't generate, for example because the module isn't `partial` or a constructor parameter type isn't public ([AWT230](../../09-diagnostics.md#awt230)).

### Open generics

An [open generic](../06-open-generics.md) registration, decorator or composite of a type another assembly can't construct, or whose `[Inject]` property it can't see, gets a generic factory. It has the open type's type parameters and their constraints, and a container closes it for each closing its graph uses:

```csharp
internal sealed class OrderRepository<T>(ITimeSystem time) : IRepository<T> where T : class;   // stays internal

[Module]
[Scoped(typeof(OrderRepository<>), typeof(IRepository<>))]
public static partial class RepositoryModule;
// generated: public static IRepository<T> Registration_OrderRepository_…<T>(ITimeSystem time) where T : class => new OrderRepository<T>(time);
```

- Each closing behaves like one the container constructs itself: lifetimes, keys, `Fallback`, `WhenInjectedInto` and the constraint check ([AWT126](../../09-diagnostics.md#awt126)) apply per closing, a decorator keeps its place in the chain, and a composite fronts the other registrations of its closing.
- The container reads the async initialization and disposal of each closed type off the library, with the module's [async initializer conventions](../../04-core-concepts/03-async-initialization.md#which-conventions-apply), so a convention that matches only some closings runs on exactly those.
- A container in the module's own assembly, or one granted `InternalsVisibleTo`, calls the factory for an open type private to the module, which it can't construct either.
- A closed registration of a closing on the container, another module or the base of an extending container, such as `[Singleton<Repository<Order>>]` beside the module's `[Singleton(typeof(Repository<>))]`, calls the generic factory too, so it shares the closing's instance, as in the library's own assembly.
- A type the module registers both closed and as an open generic stays one instance per closing: the container builds it through whichever of the two factories returns a type that serves all of its services, and reports [AWT231](../../09-diagnostics.md#awt231) when neither does.

An `OnActivated` or `OnRelease` hook of an open registration gets a generic wrapper beside the factory, with the same type parameters and constraints, which the container closes for each closing too. The module binds a generic hook to the open type's own type parameters, as a container binds it to each closing, so the hook may take the internal type and further parameters that use them:

```csharp
internal sealed class AuditedRepository<T> : IRepository<T> where T : class;   // stays internal

[Module]
[Scoped(typeof(AuditedRepository<>), typeof(IRepository<>), OnActivated = nameof(Track))]
public static partial class AuditModule
{
    internal static void Track<T>(AuditedRepository<T> repository, ITimeSystem time) where T : class { }
}
// generated: public static void Hook_OnActivated_AuditedRepository_…<T>(IRepository<T> instance, ITimeSystem time) where T : class
//     => Track<T>((AuditedRepository<T>)instance, time);
```

A public open type with an `internal` hook needs no factory, because another assembly constructs it, but gets the same generic wrapper for the hook, which a container in another assembly closes and calls in place of the hook.

The module never fails its build over an open generic. When it can't generate the factory, it records why, and a container in another assembly reports [AWT231](../../09-diagnostics.md#awt231) with the reason:

- a lifecycle hook the wrapper can't run,
- a constraint or constructor parameter type another assembly can't name,
- a type parameter named like a constructor parameter or injected property,
- a convention of the module that no wrapper can run,
- a module that isn't `partial`.

<details>
<summary>Generic hooks and their overloads</summary>

A generic hook whose constraints the open type's type parameters don't satisfy, whose type parameters come in another order, or whose parameters the wrapper can't mirror, keeps the factory out with the reason. Overloads of the hook add these rules:

- Another overload of the hook that accepts only some closings, such as an overload for `IRepository<Order>`, keeps the factory out too: a container in the library's own assembly reports the hook of such a closing as ambiguous ([AWT190](../../09-diagnostics.md#awt190)), while a container in another assembly may not see the overload, so it can't tell which closings it accepts.
- The factory of a type private to the module stays `internal` then, for the containers of the library's own assembly and of the assemblies it grants `InternalsVisibleTo`, which report the ambiguous closings themselves. A `private` overload, which such an assembly doesn't see either, makes it report [AWT231](../../09-diagnostics.md#awt231) instead.
- A public open type gets no hook wrapper then. When one of the two overloads isn't public, the module records the hook, and a container in another assembly reports [AWT231](../../09-diagnostics.md#awt231) instead of calling the overload it sees, since it can't tell which closings the other one accepts.
- A [base container](../09-extending-containers.md) does the same for a `private` overload of the hook of a type private to it, which a container that extends it in another assembly reports as [AWT226](../../09-diagnostics.md#awt226).

</details>

### Closings over internal types

An internal implementation may take a public generic service closed over itself, such as the `ILogger<PourOver>` the generic host gives every class. A container in another assembly can't name that closing, and only the container knows who provides `ILogger<>`, so the module passes the closing on through a generated closing handle and leaves the choice to the container:

```csharp
using Microsoft.Extensions.Logging;

internal sealed class PourOver(ILogger<PourOver> logger) : IBrewer;   // stays internal

[Module]
[Transient<PourOver, IBrewer>]
public static partial class BrewingModule;

// In the application:
[Container]
[Import(typeof(BrewingModule))]
[ImportService(typeof(ILogger<>))]   // the host provides ILogger<PourOver>
public static partial class CoffeeShop;
```

The container decides who provides the closing:

| The container | What happens |
|---|---|
| imports the definition from its host, with `[ImportService(typeof(ILogger<>))]` or with `[ImportServices]` when nothing registers it | It asks its external resolver for the closed type the handle records and passes the host's instance on. As for any injected property, `[ImportServices]` doesn't cover an `[Inject]` property. A `[FromKey]` key is forwarded. The host owns the instance, so the container never disposes it. |
| keeps the module's own open registration of the definition, such as `[Singleton(typeof(Log<>), typeof(ILog<>))]` | It builds the closing through a factory the module generated from that registration. It has the registration's lifetime, and the container disposes it. Only an unkeyed closing is built this way, so a keyed one comes from the host. |
| imports the definition with `[ImportService]` and also registers it, itself or through a module | [AWT175](../../09-diagnostics.md#awt175), as it does for a closing it can name, while `[ImportServices]` leaves the module's registration in place. |
| lives in the module's own assembly, or in one it grants `InternalsVisibleTo` | It closes the definition itself. |

Anything else is [AWT253](../../09-diagnostics.md#awt253) in the container:

- nothing provides the definition,
- the container or another module registers it and would have to close it over a type it can't name,
- the container decorates or composes it, which couldn't apply to that closing.

What a closing handle covers:

- A direct parameter of a constructor, a factory, an `OnActivated` or `OnRelease` hook, a `When` or `EagerWhen` predicate or an async initializer convention goes through a closing handle, and so does an `[Inject]` property, also a deferred one.
- The module's factory can't run a hook, a generic factory, a convention of the module or a decorator of the module on the closing, so for such a registration the module records why, and a container that keeps the registration reports it.
- A relationship or collection over the closing, an `[Arg]` of it and a closing over a type private to the module are still [AWT195](../../09-diagnostics.md#awt195), and an optional injected property of it is [AWT200](../../09-diagnostics.md#awt200).

## Non-public members

A module's `Factory` and `Instance` members, its `OnActivated` and `OnRelease` hooks, the `When` predicates of its decorators and the `EagerWhen` predicates of its singletons don't have to be public. Awaiten calls the members qualified by the module type, and for a member another assembly can't call, the module generates a wrapper:

| Member | Container in the library's own assembly or one the library grants `InternalsVisibleTo` | Container in any other assembly |
|---|---|---|
| `internal` `Factory` or `Instance` member of a public, `partial` module | calls the member | calls a generated public wrapper |
| `private` `Factory` or `Instance` member of a public service | calls the public wrapper, or the internal wrapper that returns the internal implementation when the public wrapper returns the public service in its place | calls a generated public wrapper |
| generic `Factory` member of an open registration (no wrapper) | calls it when it is public or `internal` | calls it only when it is public; a `private` one is reported in the library's build ([AWT153](../../09-diagnostics.md#awt153)) |
| `internal` `OnActivated` or `OnRelease` hook | calls the hook directly | calls a generated public wrapper |
| `internal` `When` predicate of a [conditional decorator](../03-decorators.md#apply-a-decorator-conditionally) or `EagerWhen` predicate of a [singleton](../../04-core-concepts/01-lifetimes.md#build-a-singleton-at-startup) | calls the predicate | calls a generated public wrapper, which takes an internal service through its handle ([AWT247](../../09-diagnostics.md#awt247) with the reason when the module can't generate it) |
| `private` member of an `internal`, `partial` module | calls a generated internal wrapper | can't import the module |

### Factory and instance members

- A wrapper works when the member's parameter types are public.
- A generic member can't produce a closed registration at all ([AWT108](../../09-diagnostics.md#awt108)).
- A member that isn't public and either returns an internal implementation registered under several public services or takes a parameter of an internal service the module registers gets no public wrapper: a container in another assembly reaches it through a [generated handle](#services-only-the-module-uses) instead.
- When the module can do neither, for example because no module of the library registers the internal parameter type, it records why, and a container in another assembly names that cause in [AWT108](../../09-diagnostics.md#awt108), or [AWT109](../../09-diagnostics.md#awt109) for an `Instance` member.
- Overloads of a private member are reported in the library's build ([AWT112](../../09-diagnostics.md#awt112)), as is a private member the module can't wrap at all ([AWT153](../../09-diagnostics.md#awt153)), and a module that is not `partial` reports any private member there ([AWT194](../../09-diagnostics.md#awt194)). A public overload that produces another type doesn't count as the member.
- A member that returns an internal implementation, also as `Task<T>` or `ValueTask<T>`, gets a wrapper that returns the public service it is registered as, as long as the registrations of that member name one public service and every hook they name is public and takes that service.
- A container in another assembly still matches the module's [async initializer conventions](../../04-core-concepts/03-async-initialization.md#which-conventions-apply) and reads the disposal against the implementation, so it initializes and disposes the instance as a container in the library's own assembly does. A matching convention whose type or method it can't call runs through a wrapper the module generates ([AWT234](../../09-diagnostics.md#awt234) when there is none).
- An `internal`, `partial` module may use a private member too: only its own assembly and the assemblies the library grants `InternalsVisibleTo` can import it, so it generates an internal wrapper for the member, which their containers call.

### Async initialization behind a factory

A synchronous `Factory` member of the module whose body constructs an async-initialized type behind a plainer return type hides that initialization ([AWT106](../../09-diagnostics.md#awt106)). The library's own build reports it, because a container in another assembly, or in one the library grants `InternalsVisibleTo`, sees no body. A container in the library's own assembly leaves it to that build, so every container agrees.

- Declare the constructed type as the factory's return type, or make the factory `async` and initialize the instance inside it.
- When that type is private to the module, no container can name it as the return type ([AWT234](../../09-diagnostics.md#awt234)), so make the factory `async` or make the type `internal`.

### Hooks

- An `OnActivated` or `OnRelease` hook can be `internal` too, beside a public overload that takes another type, on a `Factory` registration and on a registration of a public implementation that the container constructs.
- The wrapper of an asynchronous hook returns the hook's task, which the container awaits as it awaits the hook itself.
- The wrapper takes an internal service the library registers through its [generated handle](#services-only-the-module-uses). Any other parameter type must be public ([AWT203](../../09-diagnostics.md#awt203)) and carry no `[FromKey]` or `[Inject]` ([AWT204](../../09-diagnostics.md#awt204)).
- A hook the wrapper can't expose gets no wrapper and no error in the library's build when a container can still call it directly:
  - a hook of a constructed registration, which a container in the library's own assembly calls,
  - a hook whose parameter needs an internal service that a module of the library registers but can't build,
  - any hook when the library grants `InternalsVisibleTo` to an assembly.
- A container that can call neither the hook nor a wrapper reports [AWT231](../../09-diagnostics.md#awt231).
- A public hook beside an `internal` overload that accepts the implementation too is ambiguous where both are visible ([AWT190](../../09-diagnostics.md#awt190)), so the module records it, and a container in another assembly that sees only the public overload reports [AWT231](../../09-diagnostics.md#awt231) instead of calling it, or [AWT226](../../09-diagnostics.md#awt226) when it inherits the module's import from a [base container](../09-extending-containers.md) in another assembly.
- A `private` hook is no hook for a type a container constructs ([AWT164](../../09-diagnostics.md#awt164)).

## Services only the module uses

A module can register a service under an internal type that only its own implementations depend on. A container in another assembly can't name that type, but it still builds, shares and disposes the service.

```csharp
public interface IGrinder;
internal sealed class BeanStore;                           // internal service
internal sealed class Grinder(BeanStore beans) : IGrinder;

[Module]
[Singleton<BeanStore>]
[Singleton<Grinder, IGrinder>]
public static partial class CoffeeModule;
```

A container in another assembly imports `CoffeeModule` and resolves `IGrinder`. It never sees `BeanStore`.

An `internal` factory method or lifecycle hook of the module can take the internal service too:

```csharp
public interface IMill;
public sealed class Roastery;
internal sealed class Mill(BeanStore beans) : IMill;

[Module]
[Singleton<BeanStore>]
[Singleton<IMill>(Factory = nameof(CreateMill))]
[Singleton<Roastery>(OnActivated = nameof(Preheat))]
public static partial class RoasteryModule
{
    internal static IMill CreateMill(BeanStore beans) => new Mill(beans);   // built through an instance handle

    internal static void Preheat(Roastery roastery, BeanStore beans) { }   // called through a wrapper that takes the handle
}
```

### Handles

- For each such service, the module generates a public handle class in its `Internals` class. The handle holds the instance, and only the module's assembly can create or unwrap it. The factory of `Grinder` takes the handle and passes the `BeanStore` it holds to the constructor.
- The module also generates an instance handle for each implementation it builds this way. The container registers the instance handle once, with the lifetime and other options of the implementation's registrations, and serves every service of the implementation from it. So it checks lifetimes, async initialization and missing dependencies through it as for any service.
- The registrations of one implementation must agree on their lifetime, production and directives, as in the library's own assembly. The library's build reports a conflict ([AWT107](../../09-diagnostics.md#awt107), [AWT111](../../09-diagnostics.md#awt111), [AWT166](../../09-diagnostics.md#awt166)).
- An overridable default is left to the container: one it overrides contributes neither its lifetime nor a directive, such as `Eager`, and one it keeps is checked there, also against the registrations of other modules. When the container overrides every registration of the implementation, it doesn't build the implementation at all, so its dependencies, hooks and async initialization don't apply.
- A handle is a dependency only. The container never resolves it directly, never lists it in `Registrations`, and never adds it to an `IServiceCollection`. Diagnostics name the service instead of the handle, for example `'Lib.BeanStore (internal to module Lib.CoffeeModule)'`.

### How the container treats an internal service

The container treats the internal service like one it constructs itself:

- A singleton is shared by all its dependents, a scoped service is built once per scope, and a transient once per dependency.
- The container disposes the instance, synchronously or asynchronously, as the implementation implements it. `SuppressDisposal` works as usual.
- `OnActivated` and `OnRelease` hooks run through generated wrappers, and so do the module's [async initializer conventions](../../04-core-concepts/03-async-initialization.md#which-conventions-apply). The hooks and conventions may stay internal.
- An internal service may depend on other internal services of the module, and an `Eager` or async-initialized internal singleton is built even when nothing public depends on it. A module that isn't `partial` can't generate its handle and is reported ([AWT194](../../09-diagnostics.md#awt194)). When its assembly grants `InternalsVisibleTo`, the library build leaves the singleton to those assemblies instead, and any other container reports that it can't build it ([AWT231](../../09-diagnostics.md#awt231)).

### How a dependent may take it

| Dependency shape | Allowed |
|---|---|
| `T`, `Func<T>`, `Lazy<T>`, `Owned<T>`, `Func<Owned<T>>`, a collection of `T` | yes |
| `Func<TArg..., T>`, `Func<TArg..., Owned<T>>` with [runtime arguments](../../06-resolution/06-runtime-arguments.md) | yes, as long as a container in another assembly can name the argument types ([AWT195](../../09-diagnostics.md#awt195) otherwise) |
| any other shape, such as `Task<T>` or `Func<TArg..., Task<T>>` | no ([AWT237](../../09-diagnostics.md#awt237)) |

- For a factory with runtime arguments, the container passes a factory of the handle with the same arguments, and builds the internal service from them like any service with `[Arg]` parameters.
- An unkeyed collection of a variant interface holds a registration of a service that converts to its element type, as in the library's own assembly: a collection of `IHandler<Order>` for a contravariant `IHandler<in T>` takes the handle of `IHandler<Message>` when a module of the library registers only that one.

### One instance per implementation

- One implementation registered under an internal service and under other services is one instance, since the container serves every service from its instance handle. The same happens for an internal implementation registered under several public services that no public type covers.
- A public implementation that a module builds through a handle is one instance too when the container, or a module of another assembly, registers it as well. The container serves that registration from the handle, and reports a directive the handle can't apply to the instance ([AWT241](../../09-diagnostics.md#awt241)).
- A module `Factory` or `Instance` that names only its service, such as `[Singleton<ITimeSystem>(Factory = nameof(CreateTimeSystem))]` with a factory that takes an internal service, names no implementation to share: a `Factory` or `Instance` of that service on the container is a second producer of it, as in the library's own assembly, so the container's own registration wins single resolution.
- A registration that replaces the module's, on an [extending container](../09-extending-containers.md#a-replacement-replaces-in-full) or over an [overridable default](./index.md#overridable-defaults), never runs the module's factory, whatever its lifetime or production.
- Several such members of one service, in one module or in several modules of the library, stay several producers of it too: each has handles of its own, so a container in another assembly keeps each one as a member of the collection, as the library's own assembly does. Only the modules the container imports count: when one of them constructs the class that such members produce, as `[Singleton<RealTimeSystem>]` does, or matches it with a `[Scan]`, the members produce that one instance and conflict ([AWT111](../../09-diagnostics.md#awt111)), and when none does, they stay several producers, whatever other modules of the library register.
- A module that constructs an implementation it registers by constructor still constructs it when a container in another assembly calls its generated factory instead, so a `Factory` of that implementation on the container conflicts with it ([AWT111](../../09-diagnostics.md#awt111)), as in the library's own assembly.

### Several modules in one library

- All modules of an assembly share one handle per internal service. The module first by fully qualified name among those that register the service declares it.
- A module may take an internal service that only another module registers, and a container in another assembly then needs to import both modules ([AWT238](../../09-diagnostics.md#awt238)).
- When two modules register the same implementation, a container in another assembly builds one instance, as a container in the library's own assembly does. Both modules build it into one instance handle, which the module first by fully qualified name among those that register the implementation declares. This holds under whichever services they register it, so a module that registers it only under a public service uses the instance handle too.
- The container takes the hooks, conventions and other options from the module it imports. When it imports several of them, the first import wins and a conflicting directive is reported ([AWT166](../../09-diagnostics.md#awt166)), as in the library's own assembly.
- In an assembly with several modules, a module builds an internal service through a handle only when a container in another assembly can reach it, so it generates no public handle that no container could use. That is the case when:
  - its implementation is public, so a container can register it too,
  - the implementation is registered under an accessible service as well,
  - it is an `Eager` or async-initialized singleton,
  - a generated member of any module takes the service,
  - a scan of another module serves the implementation from its instance handle,
  - or another module registers the same implementation.
- A module that isn't `partial`, or can't build the service, even with the internal services other modules share, can't share its handle. When another module that registers the service can, a container in another assembly takes the service from that module when it imports it. When none can, the library build reports [AWT236](../../09-diagnostics.md#awt236).
- A module that can't build its implementation of a service that another module registers as well reports why ([AWT195](../../09-diagnostics.md#awt195)), because a container in another assembly would otherwise leave that registration out and serve the other module's implementation instead.

### Decorating an internal service

- A `[Decorate]` of the module may decorate an internal service, with an internal or a public decorator, also one that another module of the assembly registers. The decorator's inner parameter may take the service or a base type of it, such as a public interface the internal service extends.
- The module builds each decorator into a handle of the service, so the container keeps the order, keys and lifetime of the chain, chains the decorators of several modules in the order it imports them, and initializes and disposes each link on its own.
- When the implementation is registered under other services too, each of them is decorated only by its own decorators, around the one instance, and a service without decorators serves the undecorated instance, as in the library's own assembly.
- A `[Composite]` over an internal service, with an internal composite or one private to the module, is built into a handle of the service the same way, and the container fronts the service's other registrations with it.
- An open generic `[Decorate]` or `[Composite]` applies to a closing the module registers under an internal type too, such as `IRepository<Secret>` for an internal `Secret`: a container in another assembly can't name that closing, so the module closes the decorator or composite onto it in its own build and builds it into the handle.

### Factory or instance of an internal service

An internal service registered with `Factory` or `Instance` gets a handle too. The module generates a public wrapper that calls the member and puts its result into the handle.

```csharp
internal interface IWaterHeater;

[Module]
[Singleton<IWaterHeater>(Factory = nameof(Make))]   // not [Singleton<HeaterModule.Heater, IWaterHeater>]
public static partial class HeaterModule
{
    private sealed class Heater : IWaterHeater;

    private static Heater Make() => new();
}
```

- The factory's parameters may be internal services as well, and an async factory gets an async wrapper.
- The handle disposes what the factory returned, as the container would, and never disposes an `Instance`.
- The module's conventions initialize what the factory declares it returns, as in every other container (see [async factories](../../04-core-concepts/03-async-initialization.md#async-factories)).
- A container in another assembly leaves out an internal service with an `internal` or private member that nothing public needs, since it could neither call the member nor name the service.
- An internal service registered as an open generic is still reported as [AWT195](../../09-diagnostics.md#awt195) when a public registration needs it. The same goes for an internal service whose own implementation or factory the module can't wrap.
- When one of the module's scans matches an implementation the module builds through a handle, the scan's factory would build a second instance, so the library build reports it ([AWT197](../../09-diagnostics.md#awt197)).
- The `Factory` or `Instance` member of an internal service may be private: the module generates an internal wrapper for it, which a container in an assembly the library grants `InternalsVisibleTo` and a container in the library's own assembly call instead, so each produces, shares, hooks, initializes and disposes the service like a container in another assembly. Its hooks must be reachable from the library's assembly ([AWT164](../../09-diagnostics.md#awt164)).
- The member may return a type private to the module. No container can name that type, so the wrapper returns the registered type, and every container treats the member as returning it: a convention that initializes the private type but not the registered type would never run, so the library's build reports it ([AWT234](../../09-diagnostics.md#awt234)).
- The library's build also reports what no container can use: a registered type private to the module ([AWT216](../../09-diagnostics.md#awt216)), which no container could hold the instance as, and a parameter of a type private to the module ([AWT195](../../09-diagnostics.md#awt195)), which no container can resolve.

### Private types that take an internal service

- For a registered type, scan match, decorator or composite private to the module, which only the module's factory can build, the module also generates an internal factory that takes the internal services directly. A container in the library's own assembly or in an assembly the library grants `InternalsVisibleTo` calls it, so the type shares the services the container builds.
- A container in the library's own assembly constructs any other scan match that takes an internal service itself.
- The hooks of such a type can't take an internal service ([AWT203](../../09-diagnostics.md#awt203)), because their one wrapper serves both factories.

## Self-compiled scans

A `[Scan]` on a `[Module]` reaches internal implementations the same way: the module compiles its own scan **in its own build**, emitting a factory per match that constructs the implementation (which its own assembly can see) and returns the accessible interface. A consuming container reads each match like a container [`[Scan]`](../07-scanning.md) match, so a self-compiled scan behaves as close to a container scan as the assembly boundary allows, including the async initialization and disposal of a match it cannot see.

```csharp
internal sealed class Roaster(ITimeSystem time) : IPlugin, IRoaster;   // stays internal

[Module]
[Scan<IPlugin>(As = ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]
public static partial class PluginModule;   // partial, so the generator can add the factory
```

A consuming container `[Import]`s the module and resolves `IRoaster` without ever naming `Roaster`. Each match is registered like a container scan match:

- An explicit registration of the same service in the container, or in another module, overrides it, as does the container's own `[Scan]`.
- Several matches that expose the same interface collect: a `[Scan<IPlugin>(As = ScanAs.Marker)]` over two internal plug-ins resolves as `IEnumerable<IPlugin>`, exactly as it would on a container.

### Private matches

A match can also be a private type nested in the module, or in a class that contains the module. The module can build such a type, but other code in the assembly can't, so every container calls the module's factory, even a container in the same assembly. Its hooks can be private too.

```csharp
[Module]
[Scan<IPlugin>(As = ScanAs.Marker)]
public static partial class TestPluginModule
{
    private sealed class FakePlugin : IPlugin;
}
```

- A private match that the scan registers as itself is skipped with a warning ([AWT196](../../09-diagnostics.md#awt196)).
- So is a private match that the module also registers explicitly ([AWT193](../../09-diagnostics.md#awt193)), because the type's single factory already serves that registration.

### Scan rules

The library's build checks these, besides the [rules every library module follows](./01-library-modules.md#rules):

- The scan sweeps the module's own assembly only ([AWT202](../../09-diagnostics.md#awt202)). This holds even when the library grants `InternalsVisibleTo`, because a container reads the matches from what the generator adds to the module.
- A match registers through accessible interfaces: a match whose exposures are all inaccessible is skipped with a warning ([AWT196](../../09-diagnostics.md#awt196)), and one with several shares its instance through a generated handle (see [several interfaces](#several-interfaces)).
- A match with no exposure at all, such as no `I` + name interface under `ScanAs.MatchingInterface`, is skipped and reported as on a container scan ([AWT139](../../09-diagnostics.md#awt139), [AWT182](../../09-diagnostics.md#awt182)), and silently by a markerless scan.
- For an `internal` module, an internal interface is accessible, and so is an internal match registered as itself, because only the module's assembly and the assemblies it grants `InternalsVisibleTo` can import it. Its hooks may take internal parameter types, and its conventions may be internal, for the same reason.
- A match that the module also registers and that every assembly can construct, because it and a constructor are public, gets no factory. A container constructs it for the registration and the scan alike, so they share one instance.
- A match that the module also produces through a `Factory` or `Instance` member shares the instance of that member in every container, also when the member takes an internal service and a container in another assembly reaches it through a [generated handle](#services-only-the-module-uses).
- A match's constructor parameters must be types a consumer can name ([AWT195](../../09-diagnostics.md#awt195)). An `[Arg]` or `[FromKey]` on a parameter is copied onto the factory, but a match with an `[Inject]` property is rejected ([AWT200](../../09-diagnostics.md#awt200)).

`SkipUnconstructable` works as on a container scan, and the consuming container applies it: it travels with each generated registration, so a container that cannot satisfy a match's dependencies drops the match with a warning ([AWT141](../../09-diagnostics.md#awt141)) instead of failing the build.

### Hooks on a module scan

`OnActivated` and `OnRelease` work on a module scan too, and the hook method may stay `internal` alongside the implementation. The module resolves the hook (closing a generic one over the match's marker) in its own build and emits a small `public` wrapper next to each factory, so a consumer runs the hook without ever naming the internal method or type.

Only the hook's parameters *after* the instance carry a rule the container form does not: they are resolved from the consuming container's graph, so they follow the rules of any [hook wrapper](#hooks).

### Scans in the container's own assembly

When the module lives in the **same assembly as the container**, there is no assembly boundary to bridge, and the container expands the module's `[Scan]` directly. The scan still means exactly what it means across assemblies: the same accessible exposures per match, one instance across them, the same rules, and construction through the same constructor the generated factory would mirror, so moving a module between the container's project and a library never changes what its scan registers.

- A match skipped in the module's build (AWT139, AWT182, AWT196) stays skipped here too.
- The only difference is mechanical: the container constructs the match directly instead of calling the generated factory, which it cannot see within its own compilation, except for a match private to the module or a class that contains it, which it builds through the factory the module generates in the same build.
- The self-compiled factories are still generated into the module, so the same module keeps working for any *other* assembly that imports it.

### Several interfaces

A match that the module's scans expose under several interfaces, through one scan's `As` or through several scans of the module, is built through a generated instance handle, like an internal implementation registered under several services (see [services only the module uses](#services-only-the-module-uses)). The module's factory wraps the instance in the handle, and a generated alias serves each interface from it, so a container in another assembly shares one instance across the interfaces: one per root for a singleton, one per scope for a scoped match and a new one per resolution for a transient match. The hooks and the module's [async initializer conventions](../../04-core-concepts/03-async-initialization.md#which-conventions-apply) run once on that instance through generated wrappers, the container disposes it once, a decorator wraps only its own interface, and the match joins the collection of each interface.

```csharp
internal sealed class Roaster(ITimeSystem time) : IPlugin, IRoaster;   // stays internal

[Module]
[Scan<IPlugin>(As = ScanAs.Marker | ScanAs.MatchingInterface, Lifetime = Lifetime.Singleton)]
public static partial class PluginModule;   // IPlugin and IRoaster resolve to one Roaster
```

- A container in the module's own assembly constructs the match itself under each interface, so it shares one instance as well.
- When a container registers a public match itself, the registration wins over the scan in any assembly: the shared instance takes the registration's lifetime, and the scan's hooks don't run on it.
- A container's own `[Scan]` that matches a public match shares the instance too, and so does the scan of another module of the assembly that matches it under one interface. Among scans, the first decides the lifetime in any assembly: the container's scans come before the modules', which come in import order, and a later scan with another lifetime is [AWT142](../../09-diagnostics.md#awt142). The module's hooks still run on the instance, but in another assembly the handle can't run a hook that such another scan sets ([AWT241](../../09-diagnostics.md#awt241)).
- A handle can't serve every match. A match private to the module or to a class that contains it has no type a handle could hold, and a match that a module of the assembly also registers would get a second instance beside the registration's, so the library build rejects both ([AWT197](../../09-diagnostics.md#awt197)).
- When the scans of two modules of the assembly each expose the same match under several interfaces, each module builds it into a handle of its own, so a container in another assembly that imports both reports [AWT240](../../09-diagnostics.md#awt240) and drops the matches of the later module.
- When the library grants `InternalsVisibleTo` to any assembly, the module records such a match instead (see [when the library grants `InternalsVisibleTo`](#when-the-library-grants-internalsvisibleto)), and a container in an assembly granted `InternalsVisibleTo` constructs it itself.

### Shared instances

A match shares one instance with every other registration or scan match of the same implementation, in any assembly. This holds when the module registers the implementation through a `Factory`, when another module, the container or the base of an [extended container](../09-extending-containers.md) registers it, and when the scans of two modules match it. Each generated registration names its implementation, so a container in another assembly builds the instance once, through the registration that wins, and the match joins it. Types of the same full name from different assemblies are different implementations, so they never share an instance.

```csharp
public interface IAppliance;
public interface IBoiler;
internal sealed class Boiler : IAppliance, IBoiler;

[Module]
[Singleton<Boiler, IBoiler>]
public static partial class BoilerModule;

[Module]
[Scan<IAppliance>(As = ScanAs.Marker, Lifetime = Lifetime.Singleton)]
public static partial class ApplianceModule;   // a container that imports both shares one Boiler
```

- The shared instance has the type the winning registration produces. Here that is `IBoiler`, the type the registration's factory returns, which doesn't convert to `IAppliance`. A container that can construct the implementation, because it is public or the library grants the container's assembly `InternalsVisibleTo`, then constructs it once for the registration and the match. Any other container reports [AWT240](../../09-diagnostics.md#awt240) and drops the match, because the match's factory would build a second instance.
- When another module builds the implementation through a [generated handle](#services-only-the-module-uses), the handle hides the instance. The scanning module then generates an alias that serves the match from the handle, so a container in another assembly that imports both modules shares the handle's instance. A container that doesn't import the module with the handle builds the match through its own factory.

## When the library grants InternalsVisibleTo

Some of the rules above relax when the library grants `InternalsVisibleTo` to any assembly. Instead of failing the library's build, the module generates no factory or wrapper and records the reason. Then:

| Container | What happens |
|---|---|
| in an assembly the library grants `InternalsVisibleTo` | It constructs the implementation itself, like any type it can reach, and registers a recorded scan match itself, exactly as the scan would, so one instance serves every service, and calls its hooks. |
| in any other assembly | It reports [AWT231](../../09-diagnostics.md#awt231) at its `[Import]`, with the reason. |

For registrations:

- When the factory can't support an implementation, the module generates no factory for it and reports no error. It records the reason instead. This covers:
  - a parameter type another assembly can't name, or an internal service the library can't build ([AWT195](../../09-diagnostics.md#awt195)),
  - several services without a covering type ([AWT197](../../09-diagnostics.md#awt197)),
  - an `[Inject]` property the factory can't mirror ([AWT200](../../09-diagnostics.md#awt200)), except a property without a setter the module can reach, which no container can set either, so it stays an error,
  - an async initializer convention no wrapper can run ([AWT234](../../09-diagnostics.md#awt234)),
  - an internal service that only another module registers ([AWT236](../../09-diagnostics.md#awt236)),
  - an internal service taken in a shape the factory can't pass on ([AWT237](../../09-diagnostics.md#awt237)).
- The same goes for a public decorator or composite whose `internal` `[Inject]` property its factory can't set.
- The same goes for an implementation that one of the module's scans also matches: the module generates neither the registration's factory nor the scan's, and records both.
- A hook whose wrapper can't mirror its parameters ([AWT203](../../09-diagnostics.md#awt203), [AWT204](../../09-diagnostics.md#awt204)) gets no wrapper the same way, unless the hook is generic.
- The same goes for a module without a `[Scan]` that is generic or not `partial`, so it can't carry any factory ([AWT194](../../09-diagnostics.md#awt194), [AWT201](../../09-diagnostics.md#awt201)): it builds, and a container in any other assembly reports [AWT231](../../09-diagnostics.md#awt231).
- A module that isn't `partial` but registers, decorates or composes a public type with an `[Inject]` property another assembly can't set is still [AWT194](../../09-diagnostics.md#awt194): whether a module is `partial` isn't visible in metadata, so a container in another assembly would construct the type itself and leave the property out.
- This doesn't apply to a type private to the module or to a class that contains it, because no other assembly can construct that type, so its errors stay.

For scans:

- When the factory can't support a match ([AWT195](../../09-diagnostics.md#awt195), [AWT197](../../09-diagnostics.md#awt197) or [AWT200](../../09-diagnostics.md#awt200)), the scans expose it under several interfaces, a wrapper can't mirror the parameters of its non-generic hook ([AWT203](../../09-diagnostics.md#awt203), [AWT204](../../09-diagnostics.md#awt204)), or the module also registers the match, the module generates no factory for it and reports no error. It records the match, every service the scans expose it as, its lifetime and the reason.
- A match private to the module or to a class that contains it keeps its errors.
- The scans of a library that grants `InternalsVisibleTo` take internal services through their handles too, so a container in an assembly the library grants nothing builds a match the module generates a factory for as it would without the grant.
- For each factory or hook wrapper of a match that takes a handle, the module also generates an internal one that takes the services directly, which a container in an assembly the library grants `InternalsVisibleTo` calls instead, since it builds the internal services itself.
- Such a library generates neither factory for an implementation that a scan matches too.

For services only the module uses:

- A container in an assembly the library grants `InternalsVisibleTo` constructs every type it can reach itself and registers no handles.
- An internal singleton of a module that isn't `partial` is left to those assemblies (see [how the container treats an internal service](#how-the-container-treats-an-internal-service)), and a private `Factory` or `Instance` member of an internal service is called through an internal wrapper (see [factory or instance of an internal service](#factory-or-instance-of-an-internal-service)).

## Libraries built by another Awaiten version

A container reads what the generator adds to a library only in the form its own Awaiten version writes. A library built without the Awaiten generator, or with a version that writes its generated metadata differently, behaves like this in a consuming container:

- A self-compiled scan stamps the module with a generated marker, also when the scan matched nothing. A module whose metadata carries a `[Scan]` but no marker the container reads, or no factory for an implementation the container can't construct, is an error at the `[Import]` ([AWT154](../../09-diagnostics.md#awt154)) instead of a silently missing service. So is a generated scan registration that names neither a factory nor a match the module recorded.
- The module's own build reports the mistakes of its `[Decorate]` attributes that are wrong for every importer, an empty `Keys` ([AWT123](../../09-diagnostics.md#awt123)), a decorator none of whose constructors takes the inner service ([AWT124](../../09-diagnostics.md#awt124)), an unsupported key type ([AWT170](../../09-diagnostics.md#awt170)) and `Keys` together with `AllKeys` ([AWT211](../../09-diagnostics.md#awt211)), and records that it did. For a module whose generated metadata lacks that record, the container reports them itself at its `[Import]`.
- A container finds an internal `[Inject]` property of another assembly through a marker the generator emits into that assembly. For a type of an assembly that lacks the marker, the container can't find such a property, so it never reports [AWT244](../../09-diagnostics.md#awt244) for it.
- A module records the collections its factories pass empty. For a module whose metadata lacks that record, the container doesn't report [AWT252](../../09-diagnostics.md#awt252).
- A self-compiled scan records which async initializer conventions each match needs and whether it is disposable. For a generated scan registration whose metadata lacks that record, the container goes by the type the factory returns, as for any factory.
- For a type a module builds into an instance handle, the container sets an `[InjectProperty]` entry through a member that returns the instance from the handle. A module whose metadata lacks that member makes the entry [AWT178](../../09-diagnostics.md#awt178).

Rebuild such a library with the Awaiten version the container uses.

## Where to go next

- [Shipping modules from a library](./01-library-modules.md) for the guide this page details.
- [Extending containers](../09-extending-containers.md#accessibility) for a base container in another assembly, which follows similar accessibility rules.
- [Diagnostics](../../09-diagnostics.md) for every `AWT` id on this page.
