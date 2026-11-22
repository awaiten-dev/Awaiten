---
description: Run an Awaiten container under ASP.NET Core and the generic host, use services the host registers in your graph, or migrate an IServiceCollection setup, and which page explains each.
---

# Hosting

An Awaiten container runs on its own: create the root and resolve from it. ASP.NET Core, the generic host and worker services expect Microsoft.Extensions.DependencyInjection (MS.DI) instead. The `Awaiten.Extensions.DependencyInjection` package hands your container to the host, and imported services let your container use what the host registers, such as its logger or configuration.

## Pick the page for your task

| You want to | Explained on |
|---|---|
| Hand your container to ASP.NET Core or the generic host | [MS.DI bridge](./01-msdi-bridge.md) |
| Use services that the host registers inside your Awaiten graph | [External services](./02-external-services.mdx) |
| Move an existing `IServiceCollection` setup to Awaiten, at once or one service at a time | [Migrating from MS.DI](./03-migrate-from-msdi.md) |

## Three ways to hand a container to the host

The bridge offers three entry points. The [MS.DI bridge](./01-msdi-bridge.md#which-path) compares them in detail.

- [`AddAwaitenContainer`](./01-msdi-bridge.md#project-into-a-service-collection) adds the container to a service collection, such as `builder.Services`, next to the host's own registrations. It is also the way to migrate one group of services at a time.
- [`AwaitenServiceProviderFactory`](./01-msdi-bridge.md#use-it-as-the-hosts-provider-factory) fits a host that builds its provider through a factory.
- [`AwaitenServiceProvider`](./01-msdi-bridge.md#or-let-awaiten-own-everything) lets the container answer every request, when nothing but the container registers services.

## Where to go next

- [MS.DI bridge](./01-msdi-bridge.md#a-complete-aspnet-core-app) for a complete ASP.NET Core app.
- [External services](./02-external-services.mdx) to pull the host's logger or configuration into your graph.
- [Migrating from MS.DI](./03-migrate-from-msdi.md) to move your registrations over.
