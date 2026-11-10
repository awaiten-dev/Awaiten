---
description: Register every type that matches a marker or a naming convention with one compile-time scan.
---

# Scanning

Some sets of services grow over time. Every drink on the menu implements `IDrink`. You do not want to add a registration line each time you add a drink. A scan registers every type that matches a marker, and it does it at compile time by reading metadata, so there is still no runtime reflection.

## Scan for a marker

```csharp
[Container]
// highlight-next-line
[Scan<IDrink>]
public static partial class CoffeeShop;
```

Every concrete, public, non-generic type assignable to `IDrink` is registered. The `typeof` form is equivalent:

```csharp
[Scan(typeof(IDrink))]
```

A type the container cannot name or construct is skipped with a warning, see [what a scan skips](#what-a-scan-skips).

## Options

| Option | What it does | Default |
|---|---|---|
| `As` | [How each match registers](#choose-how-they-register): as itself, under the marker, under its `IFoo` interface | `ScanAs.Self` |
| `Lifetime` | [The lifetime](#choose-the-lifetime) of every match | `Lifetime.Transient` |
| `InAssembliesOf` | [The assemblies to scan](#scan-other-assemblies) | the container's own assembly |
| `NamePatterns`, `NamespacePatterns`, `Exclude` | [Narrow the matches](#narrow-the-matches) | no filter |
| `SkipUnconstructable` | [Skip a match the container cannot construct](#leave-out-types-that-are-not-services) with a warning | `false` |
| `OnActivated`, `OnRelease` | [Lifecycle hooks](#lifecycle-hooks) for every match | none |

## Choose how they register

By default each match registers as itself. Use `As` to register under the marker interface instead, or both.

```csharp
[Scan<IDrink>(As = ScanAs.Marker)]
```

`ScanAs` is a `[Flags]` enum: the three exposures are independent and combine with `|`.

| `ScanAs` flag | Registers each match under |
|---|---|
| `Self` (default) | The concrete type |
| `Marker` | The marker interface |
| `MatchingInterface` | The interface named `I` + its own name (`Foo` → `IFoo`) |

```csharp
// Resolvable as itself, as a member of IEnumerable<IDrink>, and by its own IEspresso interface.
[Scan<IDrink>(As = ScanAs.Self | ScanAs.Marker | ScanAs.MatchingInterface)]
```

## Match the `Foo`/`IFoo` convention

`MatchingInterface` registers each match under the interface it implements whose name is `I` + the match's own name, the common .NET convention where `Foo` implements `IFoo`. Unlike a wide "as implemented interfaces" registration, it never binds a match to an incidental interface like `IDisposable`, so the composition graph stays legible.

```csharp
[Scan<IViewModel>(As = ScanAs.MatchingInterface)]
```

Here `MainViewModel : IViewModel, IMainViewModel` registers only under `IMainViewModel`.

- The match must genuinely implement the convention interface: it is selected from the type's implemented interfaces by name, not synthesized. Generic interfaces are not matched.
- A match that implements no such interface contributes no `MatchingInterface` registration. When the scan names a marker and the match would otherwise register nothing at all, that is a warning ([AWT182](../09-diagnostics.md#awt182)), or, when the interface is implemented but inaccessible to the container, a warning naming it ([AWT188](../09-diagnostics.md#awt188)).
- Combined with `Self` or `Marker`, the other exposure still registers the match, so no warning is raised.
- A match implementing several same-named interfaces with an own-namespace one prefers that one. With none, it registers under each, which a marker scan warns about ([AWT187](../09-diagnostics.md#awt187)).

## Choose the lifetime

Scans register as transient unless you say otherwise.

```csharp
[Scan<IDrink>(Lifetime = Lifetime.Singleton)]
```

If another `Lifetime` type is in scope, write `Awaiten.Lifetime.Singleton` or add a using alias.

## Scan other assemblies

By default a scan looks in the container's own assembly. Point it at others with `InAssembliesOf`.

```csharp
[Scan(typeof(IDrink), InAssembliesOf = new[] { typeof(SeasonalDrinks) })]
```

## Narrow the matches

Marker assignability is often wider than you want. Three optional filters narrow it, combined with AND:

```csharp
[Scan<IDrink>(
    NamePatterns = ["*Latte", "!Decaf*"],                      // ends in Latte, but not the Decaf ones
    NamespacePatterns = ["CoffeeShop.Menu.**", "!**.Tests"],   // under Menu, excluding test namespaces
    Exclude = [typeof(DiscontinuedFlatWhite)])]                // and never this exact type
```

- **`NamePatterns`** globs the simple type name. `*` matches any run of characters, so `"*Latte"` is ends-with, `"Iced*"` starts-with, `"*Pumpkin*"` contains, and `"Latte"` an exact name.
- **`NamespacePatterns`** globs the namespace and is segment-aware on `.`: `*` matches one segment, `**` matches zero or more. So `"CoffeeShop.Menu.**"` is that namespace and everything nested beneath it (but not the sibling `CoffeeShop.MenuLegacy`), `"CoffeeShop.Menu.*"` its immediate children only, and `"**.Tests"` any namespace ending in a `Tests` segment.
- **`Exclude`** drops types by exact identity, so an entry survives a rename and never removes a same-named type elsewhere.

How the patterns combine:

- In either pattern list a bare entry includes and a `!`-prefixed entry excludes. A candidate passes an axis when it matches some include (or the list gives none) and no exclude.
- Matching is ordinal (case-sensitive).
- A filter set that removes every match warns with [AWT172](../09-diagnostics.md#awt172), a never-applied exclusion with [AWT173](../09-diagnostics.md#awt173), and a match-everything include (`*` or `**`) with [AWT174](../09-diagnostics.md#awt174).

## Leave out types that are not services

A marker often matches types that never come from the container: a design-time view model nested in the real one, or a row view model that your code creates with `new` because it takes a model in its constructor. Two options keep them out without an `Exclude` entry for each new one.

```csharp expect=AWT141
public sealed class MenuViewModel : IViewModel
{
    public sealed class DesignTime : IViewModel;
}

public sealed class OrderLineViewModel(string product) : IViewModel;

[Container]
[Scan<IViewModel>(NamePatterns = ["!DesignTime"], SkipUnconstructable = true)]
public static partial class CoffeeShop;
```

- **`NamePatterns = ["!DesignTime"]`** excludes every type with that simple name, so each nested `DesignTime` helper stays out as long as it follows the convention.
- **`SkipUnconstructable = true`** skips a match the container cannot construct, such as `OrderLineViewModel`, which needs a `string` that nothing registers. The skip is the warning [AWT141](../09-diagnostics.md#awt141) instead of the error [AWT101](../09-diagnostics.md#awt101), so you still see what was left out. A match that is also registered explicitly is never skipped.

If you build with `TreatWarningsAsErrors` and the skips are intended, keep AWT141 a warning in the project file:

```xml
<PropertyGroup>
  <WarningsNotAsErrors>$(WarningsNotAsErrors);AWT141</WarningsNotAsErrors>
</PropertyGroup>
```

To lower it for one file only, set `dotnet_diagnostic.AWT141.severity = suggestion` in an `.editorconfig` section for that file. To silence it at one scan, put `#pragma warning disable AWT141` before the `[Scan]` and `#pragma warning restore AWT141` after it.

## Scan without a marker

Some conventions have no shared marker at all: every `Foo` has its own `IFoo` and nothing else in common. The parameterless `[Scan]` matches every concrete type instead of a marker, narrowed by the same filters.

```csharp
[Scan(As = ScanAs.MatchingInterface, NamespacePatterns = ["MyApp.Services.**"])]
```

- A markerless scan's `As` may not include `Marker`: there is no marker to register under.
- It must carry at least one `NamePatterns`, `NamespacePatterns` or `InAssembliesOf` filter so it does not sweep every concrete type in scope. A wildcard-only pattern (`*`, `**.*`) does not count: it names nothing, so it does not narrow the sweep.
- Breaking either rule is an error ([AWT183](../09-diagnostics.md#awt183)).
- Because it scans broadly, a type that does not follow the convention is simply skipped rather than warned. If the scan ends up registering nothing at all, that is a warning ([AWT184](../09-diagnostics.md#awt184)).

## Open generic markers

An unbound generic marker matches closed forms, the way Autofac's closed-types-of works. Each match registers under its closed marker interface.

```csharp
[Scan(typeof(IView<>), As = ScanAs.Marker)]
```

## Lifecycle hooks

A scan can name `OnActivated` and `OnRelease` hooks, applied to every match, so a whole family shares one activation or teardown routine without a registration line per type.

```csharp
[Container]
// highlight-next-line
[Scan<IDrink>(Lifetime = Lifetime.Singleton, OnActivated = nameof(Prime))]
public static partial class CoffeeShop
{
    private static void Prime(IDrink drink) => drink.Prime();
}
```

The hook's first parameter is the match, so it must accept every one: type it as the scanned marker (or `object`). Parameters after it are resolved from the graph exactly as for an [explicit registration's hook](../07-lifetime/03-lifecycle-hooks.md#hook-parameters), and the same rules and diagnostics apply: an unusable hook name is [AWT164](../09-diagnostics.md#awt164), an unregistered parameter is [AWT101](../09-diagnostics.md#awt101).

When two scans match the same type, their hooks merge:

- One scan's `OnActivated` combines with another's `OnRelease`, and both naming the same method is fine.
- Two scans naming *different* methods for the same slot contradict each other, so the first scan's method is used and the contradiction is surfaced as [AWT199](../09-diagnostics.md#awt199).

An [explicit registration](#overriding-a-scanned-type) of a scanned type is different:

- The match shares the instance the registration owns, which runs the registration's hooks, so a scan hook would never run on it. A scan hook that the registration does not name too is therefore an error, [AWT166](../09-diagnostics.md#awt166), whether the registration names another hook or none.
- Name the same method on the explicit registration if the special-cased type should keep the hook, or exclude the type from the scan and register it explicitly under the marker if it should not.
- This follows the type, not the service: an explicit registration of just the concrete type, say `[Transient<Espresso>]` beside a marker scan, owns the instance of `Espresso` even where the scan still supplies its marker mapping.
- It holds for every registration that shares the match's instance, a keyed one or one of an imported module included, and in every assembly: a module of another assembly that registers the type owns its instance there too.
- A scan an [extending container](./08-extending-containers.md) inherits from its base is the exception: the extending container can neither name the base's hook nor exclude a type from the base's scan, so its own registration of a matched type replaces the scan's hooks for that type without an error.

### Generic hooks on an open marker

An [open generic marker](#open-generic-markers) knows each match's closed type argument at compile time, so a hook can be *generic* and receive it directly, with no reflection and no `object`. The classic case is a WPF-style view/view-model family: bind each view to its matching view model as it is activated.

```csharp
[Container]
[Singleton<MainViewModel, IMainViewModel>]
[Scan(typeof(IView<>), As = ScanAs.Marker, OnActivated = nameof(WireView))]
public static partial class App
{
    private static void WireView<TViewModel>(IView<TViewModel> view, TViewModel viewModel)
        => view.DataContext = viewModel;
}
```

For `MainWindow : IView<IMainViewModel>` the generator dispatches `WireView<IMainViewModel>(mainWindow, viewModel)`, resolving the matching `IMainViewModel` from the graph. The type argument is visible to graph analysis, so an unregistered view model fails the build ([AWT101](../09-diagnostics.md#awt101)) rather than at runtime. A non-generic method works too (its parameters typed as the marker or `object`); the type argument only applies when the method is generic.

- A *generic* hook takes its type argument from a closed marker form, so it needs exactly one it can bind. A match offering several, by closing the marker more than once (`Dual : IView<A>, IView<B>`), leaves that argument ambiguous and is reported as [AWT198](../09-diagnostics.md#awt198): register such a type explicitly with the hook it needs, or split the family so only one closed form binds it.
- Only forms the hook could actually bind count, so a constraint (`where TViewModel : class`) that rules out all closings but one settles the choice, as does accessibility: a closing at another assembly's internal type cannot be dispatched and yields to an accessible sibling.
- If the hook name is overloaded and another overload also accepts the match, the collision is between overloads rather than closings and is reported as [AWT190](../09-diagnostics.md#awt190) instead.
- A non-generic hook takes no type argument, so a match with several closings is fine for it.

## Overriding a scanned type

An explicit registration of a scanned type wins over the scan, so you can special-case one drink while scanning the rest.

## What a scan skips

A type the container cannot name is skipped rather than registered. Since a match quietly vanishing from the container is almost never what you meant, the scan reports the skip as [AWT193](../09-diagnostics.md#awt193):

- An implementation that is `internal` to another assembly (with no `InternalsVisibleTo`), or a class nested privately in a type the container is not part of. Widening the type to `public` brings it back. For internal types of another assembly, moving the scan into a [module](./07-modules/01-library-modules.md#self-compiled-scans) of that assembly keeps them internal: the module builds them for any container that imports it.
- A `public` class of another assembly whose constructors are all `internal`: the container can name the type but cannot call a constructor, so the scan skips it with [AWT193](../09-diagnostics.md#awt193) too, naming the constructors as the reason, where an explicit registration of the type fails with [AWT104](../09-diagnostics.md#awt104). An assembly the type's assembly grants `InternalsVisibleTo` can call them, so its scan registers the type, and a module scan in the type's own assembly builds it for every container that imports the module.
- A class nested privately in the container that the scan registers as itself (`ScanAs.Self`, the default): the private type would be its own service, which the container cannot expose. The scan skips such a match with [AWT193](../09-diagnostics.md#awt193), naming the reason, instead of failing the build, and it does the same when no accessible type the match converts to covers all the interfaces it is registered under.

Only a type your scan actually asked for is reported, so the internal plumbing of a scanned assembly stays quiet, as does anything you filtered out. Whether the *interfaces* a match is exposed under are accessible is a separate question, covered in [match the `Foo`/`IFoo` convention](#match-the-fooifoo-convention).

A class nested privately in the container itself, such as a test double, is a match like any other, as long as the scan registers it under an accessible interface: the generated container hands it out as that interface, just as it does for an [explicit registration](./08-extending-containers.md) of a private type.

```csharp
[Container]
[Scan<IPlugin>(As = ScanAs.Marker)]
public static partial class TestCoffeeShop
{
    private sealed class FakePlugin : IPlugin;
}
```

## Scan from a module

A `[Scan]` may also sit on a `[Module]`, where it is compiled in the module's own build so a library can scan its `internal` implementations and expose them to consumers through their interfaces. `OnActivated` and `OnRelease` work there too, and the hook method may stay `internal`. See [self-compiled scans](./07-modules/01-library-modules.md#self-compiled-scans) for the rules, [shared instances](./07-modules/01-library-modules.md#shared-instances) for a match that another registration or scan also builds, and [several interfaces](./07-modules/01-library-modules.md#several-interfaces) for a match exposed under more than one interface.

## Diagnostics

| Id | Reported when |
|---|---|
| [AWT138](../09-diagnostics.md#awt138) | A scan matches nothing. This is a warning, not an error, so an empty menu does not break the build. |
| [AWT141](../09-diagnostics.md#awt141) | `SkipUnconstructable` skips a match the container cannot construct. |
| [AWT166](../09-diagnostics.md#awt166) | An explicit registration of a scanned type does not name a scan hook. |
| [AWT172](../09-diagnostics.md#awt172), [AWT173](../09-diagnostics.md#awt173), [AWT174](../09-diagnostics.md#awt174) | A filter removes every match, an exclusion never applies, or an include matches everything. |
| [AWT182](../09-diagnostics.md#awt182), [AWT187](../09-diagnostics.md#awt187), [AWT188](../09-diagnostics.md#awt188) | A `MatchingInterface` match registers nothing, registers under several same-named interfaces, or implements an inaccessible one. |
| [AWT183](../09-diagnostics.md#awt183), [AWT184](../09-diagnostics.md#awt184) | A markerless scan breaks its rules, or registers nothing. |
| [AWT190](../09-diagnostics.md#awt190), [AWT198](../09-diagnostics.md#awt198), [AWT199](../09-diagnostics.md#awt199) | A hook overload is ambiguous, a generic hook has several closings to bind, or two scans name different hooks. |
| [AWT193](../09-diagnostics.md#awt193) | A match the container cannot name or construct is skipped. |

:::tip[When not to reach for this]
Prefer explicit registrations. A scan trades away the property that makes the composition root useful: the whole graph visible in one place. Reach for a scan only for a large, uniform family that grows on its own, like message handlers, validators, or plug-ins, where listing each one adds churn without adding clarity. For a handful of services, spell them out, see [Design principles](../03-design-principles.md#when-power-becomes-a-smell).
:::

## Where to go next

- [Modules](./07-modules/index.md) to group registrations for reuse.
- [Shipping modules from a library](./07-modules/01-library-modules.md) to scan a library's internal implementations.
- [Open generics](./05-open-generics.md) for generic service families.
