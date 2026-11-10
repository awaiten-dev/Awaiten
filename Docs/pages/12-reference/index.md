---
description: The lookup pages of the Awaiten documentation, from the attribute and runtime API reference to the diagnostics catalog, the glossary and troubleshooting, and where to find each answer.
---

# Reference

The rest of the docs explain Awaiten one feature at a time, with the coffee shop as the running example. The reference pages are for looking something up: they list everything in one place, keep explanations short, and link back to the feature page that explains the why.

## What is here

| Page | Look here for |
|---|---|
| [Attribute reference](./01-attributes.md) | Every attribute you put on a container, a module or a domain type, where it goes, its generic and `typeof` forms, each property with its default, and the enums the attributes take. |
| [Runtime API](./02-runtime-api.md) | What you call at run time: the generated `Root` and `Scope`, `Resolve`, `TryResolve` and `ResolveAsync` with their overloads, scopes, `Owned<T>`, external resolvers, registration metadata and the MS.DI bridge. |
| [Diagnostics](../09-diagnostics.md) | Every `AWT` diagnostic, with its severity, an example that triggers it and the fix. Your IDE links each id there. |
| [Glossary](./03-glossary.md) | The terms the docs use, such as closing, async-tainted, overridable default or composition root, each in a few sentences. |
| [Troubleshooting](./04-troubleshooting.md) | Problems that are not a single diagnostic: a generator that does not run, a resolve that throws at run time, a service that resolves to an unexpected implementation. |

## Find it fast

| You want to know | Go to |
|---|---|
| Which options a `[Singleton]`, `[Scan]` or `[Decorate]` takes, and their defaults | [Registrations](./01-attributes.md#registrations) |
| What `[Container]` and `[Module]` can be configured with | [Containers and modules](./01-attributes.md#containers-and-modules) |
| Which attributes may appear on your own types | [Injection sites](./01-attributes.md#injection-sites) |
| The values of `Lifetime`, `AsyncSafety`, `LifetimeSafety`, `Fallback` or `ScanAs` | [Enums](./01-attributes.md#enums) |
| Which overload resolves what, and when it throws | [Resolving](./02-runtime-api.md#resolving) |
| What an `AWT` id means and how to fix it | [Diagnostics index](../09-diagnostics.md#index) |
| Why the build does not produce a `Root` | [Troubleshooting: build](./04-troubleshooting.md#build) |
| Why a resolve throws although the build passed | [Troubleshooting: run time](./04-troubleshooting.md#run-time) |
| How the generated code looks | [What Awaiten generates](../02-complete-example/01-generated-code.md) |

## Elsewhere on the site

- [Benchmarks](../10-benchmarks.mdx) compare Awaiten with other .NET containers on build, resolve and per-request workloads.
- The [changelog](../11-changelog.mdx) lists the breaking changes, features and fixes of each release.
