---
description: The ideas every Awaiten container builds on, how lifetimes, scopes, async initialization, lifetime safety, disposal and Owned handles fit together, and where each is explained.
---

# Core concepts

Every registration answers three questions: how long its instance lives, when it is ready to use, and who disposes it. The pages in this section give the answers, in the order they build on each other. The coffee shop runs through all of them: the espresso machine is a singleton that warms up once, each order is a scope, and each cup is a transient made fresh.

## How they fit together

| Concept | The question it answers | Explained on |
|---|---|---|
| Lifetimes | How long does an instance live, and how often is it built? You pick `[Singleton]`, `[Scoped]` or `[Transient]` per registration. | [Lifetimes](./01-lifetimes.md) |
| Scopes | Where do scoped services live, and when do they go away? The root is the outermost scope, and each unit of work opens its own. | [Scopes](./02-scopes.md) |
| Async initialization | When is a service ready? A service with asynchronous setup is used only after that setup finished. | [Async initialization](./03-async-initialization.md) |
| Lifetime safety | Can a disposable transient leak on a long-lived owner? Strict by default, loose when you want MS.DI semantics. | [Lifetime safety](./04-lifetime-safety.md) |
| Disposal | Which instances does the container dispose, and in which order? | [Disposal](./05-disposal.md) |
| `Owned<T>` | How do you get a disposable service on demand and decide yourself when it goes away? | [Owned](./06-owned.md) |

## What the compiler checks

The mistakes these concepts guard against stop the build instead of surfacing at run time:

- A service that holds on to a shorter-lived one, such as a singleton that depends on a scoped service, is [AWT105](../09-diagnostics.md#awt105). See [Lifetimes must line up](./01-lifetimes.md#lifetimes-must-line-up).
- Reaching an async-initialized service through a synchronous path is [AWT119](../09-diagnostics.md#awt119), or [AWT120](../09-diagnostics.md#awt120) when the path only depends on one. See [Async initialization](./03-async-initialization.md#reaching-an-async-service-synchronously-is-a-compile-error).
- A factory that keeps building disposables on the container root is [AWT118](../09-diagnostics.md#awt118). See [Lifetime safety](./04-lifetime-safety.md).

## Where to go next

- [Lifetimes](./01-lifetimes.md) to choose how long each service lives.
- [Async initialization](./03-async-initialization.md) for services that need asynchronous setup.
- [Registration](../05-registration/index.md) for everything else you can tell the container.
