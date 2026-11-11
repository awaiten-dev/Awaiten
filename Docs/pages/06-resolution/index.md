---
description: Every shape a dependency can take, from the service itself to Func, Lazy, Task, Owned, collections and keyed dictionaries, with what each gives you and where it works.
---

# Resolution

A consumer declares what it needs, and the container fills it. The dependency can be the service itself, a way to get it later or more than once, every implementation at once, or a dictionary of them. This section explains each shape and how you resolve services from the container at the edges of your app.

## Dependency shapes

Each shape below works as a constructor parameter, a factory parameter or an [injected property](./05-property-injection.md). `T` is the service, `TArg…` the [runtime arguments](./06-runtime-arguments.md) of a service built from them.

| Shape | What you get | Works over async-tainted `T` | Under `[FromKey]` | Resolvable by type | Explained on |
|---|---|---|---|---|---|
| `T` | The service. | Yes, the consumer becomes async-tainted too. | Yes | Yes, also with a key | [Resolving services](./01-resolving-services.md) |
| `Func<T>` | A factory that resolves `T` on each call. | No, [AWT119](../09-diagnostics.md#awt119) or [AWT120](../09-diagnostics.md#awt120) | Yes | Yes | [Relationships](./02-relationships.md) |
| `Lazy<T>` | `T`, resolved once on first access. | No, [AWT119](../09-diagnostics.md#awt119) or [AWT120](../09-diagnostics.md#awt120) | Yes | Yes | [Relationships](./02-relationships.md) |
| `Owned<T>` | `T` built in a scope of its own, disposed with the handle. | No, [AWT119](../09-diagnostics.md#awt119) or [AWT120](../09-diagnostics.md#awt120) | Yes | Yes | [Owned](../04-core-concepts/06-owned.md) |
| `Func<Owned<T>>` | A new `Owned<T>` handle on each call. | No, [AWT119](../09-diagnostics.md#awt119) or [AWT120](../09-diagnostics.md#awt120) | Yes | Yes | [Owned](../04-core-concepts/06-owned.md) |
| `Task<T>` | A task that completes once `T` is initialized. | Yes, the consumer stays synchronous. | Yes | For an async-tainted `T` | [Relationships](./02-relationships.md) |
| `Func<Task<T>>` | A factory that resolves and initializes `T` on each call. | Yes, the consumer stays synchronous. | Yes | For an async-tainted `T` | [Relationships](./02-relationships.md#resolve-a-relationship-directly) |
| `Lazy<Task<T>>` | `T`, resolved and initialized once on first access. | Yes, the consumer stays synchronous. | Yes | For an async-tainted `T` | [Relationships](./02-relationships.md#resolve-a-relationship-directly) |
| `Task<Owned<T>>` | An `Owned<T>` handle once `T` is initialized. | Yes, the consumer stays synchronous. | Yes | For an async-tainted `T` | [Owned](../04-core-concepts/06-owned.md) |
| `Func<Task<Owned<T>>>` | A new initialized `Owned<T>` handle on each call. | Yes, the consumer stays synchronous. | Yes | For an async-tainted `T` | [Owned](../04-core-concepts/06-owned.md) |
| `Func<TArg…, T>` | A factory that builds `T` from your arguments. | No, [AWT119](../09-diagnostics.md#awt119) or [AWT120](../09-diagnostics.md#awt120) | Yes | Yes | [Runtime arguments](./06-runtime-arguments.md) |
| `Func<TArg…, Owned<T>>` | A factory that builds `T` from your arguments into a handle. | No, [AWT119](../09-diagnostics.md#awt119) or [AWT120](../09-diagnostics.md#awt120) | Yes | Yes | [Runtime arguments](./06-runtime-arguments.md) |
| `Func<TArg…, Task<T>>` | A factory that builds and initializes `T` from your arguments. | Yes, the consumer stays synchronous. | Yes | Yes | [Runtime arguments](./06-runtime-arguments.md#async-initialization) |
| `Func<TArg…, Task<Owned<T>>>` | The same, handing back an `Owned<T>` handle. | Yes, the consumer stays synchronous. | Yes | Yes | [Runtime arguments](./06-runtime-arguments.md#async-initialization) |
| `IEnumerable<T>`, `T[]`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IList<T>`, `ICollection<T>` | Every registration of `T`. | No, [AWT122](../09-diagnostics.md#awt122) | Yes, the registrations under the key | Yes | [Collections](./03-collections.md) |
| `IAsyncEnumerable<T>` | Every registration of `T`, each awaited as you enumerate. | Yes, the consumer becomes async-tainted too. | Yes, the registrations under the key | Yes | [Collections](./03-collections.md#async-collections) |
| `Task<IReadOnlyList<T>>` and the `Task` of the other collection shapes | Every registration of `T` once all are initialized. | Yes, the consumer stays synchronous. | Yes, the registrations under the key | Yes | [Collections](./03-collections.md#async-collections) |
| `IReadOnlyDictionary<TKey, T>` | Every keyed registration of `T`, by its `string` or enum key. | No, [AWT122](../09-diagnostics.md#awt122) | No, [AWT160](../09-diagnostics.md#awt160) | Yes | [Keyed dictionaries](./04-keyed-dictionaries.md) |
| `Task<IReadOnlyDictionary<TKey, T>>` | The same dictionary once every member is initialized. | Yes, the consumer stays synchronous. | No, [AWT160](../09-diagnostics.md#awt160) | Yes | [Keyed dictionaries](./04-keyed-dictionaries.md#the-async-form) |
| `IReadOnlyDictionary<Type, Func<TBase>>` | A factory per closing of a generic service, by its type argument. | No, [AWT119](../09-diagnostics.md#awt119) or [AWT120](../09-diagnostics.md#awt120) | No, [AWT213](../09-diagnostics.md#awt213) | No | [Keyed dictionaries](./04-keyed-dictionaries.md#dispatch-by-runtime-type) |
| An injected property of any shape above | The shape, set on the property after construction. | As for its shape. | Yes, through `Key` or `[FromKey]` | Not applicable | [Property injection](./05-property-injection.md) |

How to read the columns:

- **Works over async-tainted `T`.** `T` is [async-tainted](../12-reference/03-glossary.md#async-tainted) when it is async-initialized or depends on an async-initialized service. A synchronous relationship reports AWT119 when `T` itself is async-initialized and AWT120 when it only depends on one. A synchronous collection or dictionary reports AWT122 for any async-tainted member. "No" holds in the strict default. With `AsyncSafety.SyncAfterInit` on the container, a synchronous shape may reach an async-initialized service after warm-up, see [Async initialization](../04-core-concepts/03-async-initialization.md#relax-the-rule-when-you-need-to).
- **Under `[FromKey]`.** The shape wraps the registration, or holds the registrations, under that key. See [Keyed services](../05-registration/05-keyed-services.md#select-directly-with-fromkey).
- **Resolvable by type.** `Resolve<…>()`, `TryResolve<…>()` or `ResolveAsync<…>()` hands out the shape from the root or a scope, with these limits:
  - A keyed resolve serves only the service itself, so every other shape under a key reaches a consumer only through `[FromKey]`.
  - The synchronous relationships come with a service that is not async-tainted and the `Task` relationships with an async-tainted one. Asking for the other throws and names the shape to use. A service with runtime arguments always comes with `Func<TArg…, Task<T>>` and `Func<TArg…, Task<Owned<T>>>`, async-tainted or not.
  - A collection shape is resolvable only when its element type has a registration, so a collection that is injected empty throws when resolved. A synchronous one also needs that no member is async-tainted.
  - `IReadOnlyDictionary<TKey, T>` and its `Task` form are resolvable only when every key of `T` is of one type, and the synchronous dictionary only when no member is async-tainted.
  - In the strict default, the root withholds a shape that builds a new disposable on each call, such as a `Func<T>` over a disposable transient, see [Relationships](./02-relationships.md#reaching-shorter-lived-services).

Some dependencies are errors:

- A service built from runtime arguments comes only through its `Func<TArg…, …>` shapes. A dependency on the service itself, `Lazy<T>`, `Owned<T>` or `Task<T>` is [AWT115](../09-diagnostics.md#awt115).
- A `Func` whose arguments do not match is [AWT113](../09-diagnostics.md#awt113).
- `Lazy<Owned<T>>` and `Lazy<Task<Owned<T>>>` are [AWT121](../09-diagnostics.md#awt121), because `Lazy` would memoize a single disposal handle. Take `Owned<T>`, `Func<Owned<T>>` or `Task<Owned<T>>` instead.
- Relationships do not nest: a relationship over a collection or a collection of relationships is an error unless that type is registered as a service, see [Relationships](./02-relationships.md#relationships-wrap-a-single-service).
- A relationship over another relationship, such as `Func<Lazy<T>>`, `Lazy<Func<T>>`, `Func<TArg…, Lazy<T>>` or `Task<Lazy<T>>`, is not built. Only the combinations in the table above are, so such a dependency is a missing dependency ([AWT101](../09-diagnostics.md#awt101)).

## Where to go next

- [Resolving services](./01-resolving-services.md) for `Resolve`, `TryResolve` and `ResolveAsync`.
- [Relationships](./02-relationships.md) for `Func`, `Lazy` and `Task`.
- [Collections](./03-collections.md) for every implementation at once.
