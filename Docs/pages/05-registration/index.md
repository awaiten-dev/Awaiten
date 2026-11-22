---
description: Every way to tell an Awaiten container what to build, from factories and lifecycle hooks to decorators, composites, keys, open generics, scanning, modules and extended containers, and which page explains each.
---

# Registration

A registration is an attribute on the container class: `[Singleton<EspressoMachine>]` tells the container to build an `EspressoMachine` and share it. [Lifetimes](../04-core-concepts/01-lifetimes.md) explain the three lifetime attributes. This section covers everything else you can tell the container: how to build a service, how to wrap or combine services, how to choose between several implementations, and how to register many types or whole bundles at once.

## Pick the page for your task

| You want to | Use | Explained on |
|---|---|---|
| Build a service with your own code, or register an object you already have | `Factory` or `Instance` on the lifetime attribute | [Factories and instances](./01-factories-and-instances.md) |
| Run code when an instance is activated or released, without putting it into the service | `OnActivated` and `OnRelease` | [Lifecycle hooks](./02-lifecycle-hooks.md) |
| Add logging, retries or caching around a service without changing it | `[Decorate]` | [Decorators](./03-decorators.md) |
| Fan one call out to every implementation of a service | `[Composite]` | [Composites](./04-composites.md) |
| Register several implementations of one service and pick one per consumer | `Key`, `WhenInjectedInto` or `[FromKey]` | [Keyed services](./05-keyed-services.md) |
| Register a generic service once for every type argument the graph uses | The `typeof` form with an unbound generic | [Open generics](./06-open-generics.md) |
| Register every type that matches a marker or a naming convention | `[Scan]` | [Scanning](./07-scanning.md) |
| Bundle registrations and reuse them in several containers | `[Module]` and `[Import]` | [Modules](./08-modules/index.md) |
| Ship a module from a library that keeps its implementations internal | `[Module]` in the library | [Shipping modules from a library](./08-modules/01-library-modules.md) |
| Reuse a production container in tests and replace only some services | `Extends` on `[Container]` | [Extending containers](./09-extending-containers.md) |

The [attribute reference](../12-reference/01-attributes.md) lists every attribute with all its options and defaults.

## Where to go next

- [Factories and instances](./01-factories-and-instances.md) when a constructor is not enough.
- [Resolution](../06-resolution/index.md) for how consumers ask for what you registered.
- [Attribute reference](../12-reference/01-attributes.md#registrations) to look up an option.
