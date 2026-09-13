# ReliableWebhooks

[![CI](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml)
[![Release](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml/badge.svg)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/ReliableWebhooks.svg)](https://www.nuget.org/packages/ReliableWebhooks/)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**English** | [Português (Brasil)](README.pt-BR.md)

ReliableWebhooks is a .NET 10 library for reliable outbound webhook delivery.

The first public release is **v1.0.0**. It provides a technology-agnostic persistence contract, bounded concurrent dispatching, lease ownership, configurable retries, HMAC-SHA256 signing, secure HTTP defaults, standard .NET observability, and DI/Generic Host integration.

## Why ReliableWebhooks?

Sending an HTTP request is easy; delivering it reliably across transient failures, process restarts, concurrent workers, retry windows, and duplicate-delivery scenarios is not. ReliableWebhooks keeps those concerns explicit and replaceable instead of hiding them inside an opaque background loop.

Core capabilities include:

- stable webhook identities and duplicate-safe enqueue semantics;
- `IWebhookDeliveryStore` as a pluggable persistence port;
- bounded concurrent dispatching with renewable leases;
- one-attempt HTTP transport with response classification;
- exponential retry/backoff, jitter, and `Retry-After` support;
- HMAC-SHA256 signing over a versioned authenticated envelope;
- HTTPS-required defaults, redirect/cookie isolation, and destination-policy support;
- structured logs, traces, and bounded metrics through standard .NET APIs;
- dependency injection and optional hosted dispatcher integration.

## Installation

The package targets `net10.0`.

```bash
dotnet add package ReliableWebhooks --version 1.0.0
```

or:

```xml
<PackageReference Include="ReliableWebhooks" Version="1.0.0" />
```

## Quick start

```csharp
using System.Text.Json;
using ReliableWebhooks;

byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
{
    OrderId = 123,
    Status = "created",
});

var message = new WebhookMessage(
    id: Guid.NewGuid().ToString("N"),
    eventType: "order.created",
    destination: new Uri("https://example.com/webhooks"),
    payload: payload,
    contentType: "application/json");

IWebhookDeliveryStore store = new InMemoryWebhookDeliveryStore();
await store.EnqueueAsync(message, DateTimeOffset.UtcNow);
```

`InMemoryWebhookDeliveryStore` is **not durable**. It is intended for tests, samples, and local development. Production restart durability requires a conforming durable `IWebhookDeliveryStore` supplied by the application.

## Dependency injection

```csharp
services.AddSingleton<IWebhookDeliveryStore, MyDurableWebhookStore>();

ReliableWebhooksBuilder webhooks = services.AddReliableWebhooks(options =>
{
    options.Dispatcher.MaxConcurrency = 8;
    options.Dispatcher.LeaseDuration = TimeSpan.FromMinutes(2);
    options.MessageLimits.MaxPayloadBytes = 1024 * 1024;
});

webhooks.AddHostedDispatcher();
```

`AddHostedDispatcher()` is opt-in. The integration depends on `Microsoft.Extensions.*` and does not require ASP.NET Core.

## Delivery guarantees

ReliableWebhooks is designed for **at-least-once delivery**, not exactly-once delivery. Receivers should therefore be idempotent.

A conforming durable store is responsible for durable enqueueing, atomic claims, lease ownership, stale-owner rejection, retry scheduling, and persistence across the durability boundary it advertises.

The core package intentionally does not require EF Core, Dapper, Redis, files, a relational database, or another specific persistence technology.

## Secure defaults

The standard integration requires HTTPS destinations by default, disables automatic redirects and cookies, validates protocol-facing message metadata, limits outbound message sizes, and supports destination authorization for deployments that accept untrusted webhook URLs.

The built-in signer uses HMAC-SHA256 with a versioned canonical envelope that authenticates timestamp, stable webhook ID, event type, content type, and exact payload bytes. Production deployments should use appropriately generated signing material and protect persisted delivery data according to their threat model.

See [Production usage](docs/production-usage.md) for the complete security, persistence, signing, retry, and operations guidance.

## Observability

ReliableWebhooks emits telemetry through standard .NET APIs and does not require a specific observability backend.

```csharp
ReliableWebhooksInstrumentation.ActivitySourceName // "ReliableWebhooks"
ReliableWebhooksInstrumentation.MeterName          // "ReliableWebhooks"
```

Built-in metrics avoid unbounded event-type cardinality by default. Applications can integrate the activity source and meter with their preferred OpenTelemetry/exporter stack.

## v1.0.0 boundaries

The first public release intentionally keeps persistence technology-agnostic:

- no built-in production durable store;
- `InMemoryWebhookDeliveryStore` remains non-durable;
- exactly-once delivery is not guaranteed;
- receiver-side idempotency remains an application responsibility;
- production persistence adapters can be implemented independently of the core package.

## Documentation

- [Production usage](docs/production-usage.md)
- [Persistence contract](docs/persistence.md)
- [v1.0.0 release contract](docs/release-v1.0.0.md)
- [SonarQube Cloud](docs/sonarqube-cloud.md)
- [Contributing](CONTRIBUTING.md)
- [Changelog](CHANGELOG.md)

## Support and license

Use [GitHub Issues](https://github.com/rodri-oliveira-dev/ReliableWebhooks/issues) for bugs, questions, and feature discussions. For security issues, follow [SECURITY.md](SECURITY.md).

ReliableWebhooks is licensed under the [MIT License](LICENSE).
