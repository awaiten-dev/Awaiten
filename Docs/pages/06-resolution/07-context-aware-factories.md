---
description: Give a factory the type of the consumer it builds for with a [RequestingType] parameter.
---

# Context-aware factories

Now and then a service should know who asked for it. The classic case is a logger named after the class that uses it. `[RequestingType]` gives a factory the consumer's type at each construction site.

## Ask for the requesting type

Add a `[RequestingType] Type` parameter to a factory method. Awaiten fills it with the type whose constructor or property is being satisfied.

```csharp
[Container]
[Singleton<Prefix>]
[Transient<ILogger>(Factory = nameof(CreateLogger))]
[Transient<Barista>]
public static partial class CoffeeShop
{
    // highlight-next-line
    private static Logger CreateLogger([RequestingType] Type? requestingType, Prefix prefix)
        => new Logger(requestingType?.FullName ?? "<root>", prefix);
}
```

Now a `Barista` that needs an `ILogger` gets one tagged with `Barista`. Every other consumer of `ILogger` gets its own, tagged with its type.

## A direct resolve passes null

When you resolve the service straight from the container, there is no consumer. The parameter is `null`, so declare it as `Type?` and handle that case. It is `null` as well behind a [conditional decorator](../05-registration/03-decorators.md#apply-a-decorator-conditionally) that is off, because the decorator would have been the consumer.

```csharp
ILogger logger = shop.Resolve<ILogger>();   // requestingType is null here
```

## Other parameters and shapes work as usual

The other factory parameters resolve from the graph as usual. `[RequestingType]` flows through `Func<T>` and through async factories too.

The requesting type is the implementation even when your container cannot name it, because a [module](../05-registration/08-modules/index.md) in another assembly or the base of an [extending container](../05-registration/09-extending-containers.md) builds an internal or private implementation, decorator, composite or scan match for it. A logger gets the same name wherever the module is imported or the container is extended.

## Rules

- The parameter must be `System.Type` ([AWT162](../09-diagnostics.md#awt162)).
- For a service built by a `Factory` registration, the requesting type is the implementation the registration names, whatever the factory method returns: `Grinder` for `[Singleton<Grinder, IGrinder>(Factory = ...)]`, the service for `[Singleton<IGrinder>(Factory = ...)]`.
- A factory cannot mix `[RequestingType]` with an `[Arg]` runtime argument ([AWT163](../09-diagnostics.md#awt163)).
- The factory runs for each consumer, so there is no `Owned<T>` over its service, in any form ([AWT186](../09-diagnostics.md#awt186)).
- A keyed resolve, such as `Resolve<ILogger>("Audit")`, throws for a service built by such a factory, even when it is registered under that key. Inject it under the key instead, through a [`[FromKey]`](../05-registration/05-keyed-services.md#select-directly-with-fromkey) parameter or property.

:::tip[When not to reach for this]
The requesting-type hook is advanced, and it makes one abstraction mean different things to different consumers, which is exactly the kind of implicit coupling DI is meant to remove. Reserve it for a genuinely cross-cutting service where the consumer's identity is the whole point, like a logger named after the class that uses it. For anything else, give each consumer the dependency it actually needs, see [Design principles](../03-design-principles.md#when-power-becomes-a-smell).
:::

## Where to go next

- [Factories and instances](../05-registration/01-factories-and-instances.md) for factories in general.
- [Property injection](./05-property-injection.md) for injecting a context-aware logger into a property.
