# ReliableWebhooks

[![CI](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml)
[![Release](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml/badge.svg)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/ReliableWebhooks.svg)](https://www.nuget.org/packages/ReliableWebhooks/)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**English** | [Português (Brasil)](README.pt-BR.md)

ReliableWebhooks is a .NET 10 library for reliable outbound webhook delivery. It provides explicit primitives for durable enqueueing, lease-based dispatch, bounded concurrency, HTTP delivery, retry classification and scheduling, HMAC-SHA256 signing, observability, and Microsoft dependency injection/hosting.

Use it when an HTTP `POST` is not enough: deliveries must survive failures, coordinate multiple workers, retry deterministically, and expose operational state without pretending that exactly-once HTTP delivery exists.

## Guarantees and boundaries

- With a conforming **durable** `IWebhookDeliveryStore`, the intended delivery model is **at-least-once**.
- ReliableWebhooks does **not** guarantee exactly-once delivery. Receivers must be idempotent and tolerate duplicates.
- The core package does **not** ship a production durable store. Durability across process restarts depends on the store supplied and configured by the consuming application.
- `InMemoryWebhookDeliveryStore` is process-local and intended only for tests, samples, and local development.
- One `IWebhookDeliveryTransport.SendAsync` call represents one HTTP attempt; retry timing is handled separately by the retry policy and dispatcher.
- Automatic dead-letter replay, receiver-side idempotency, and secret-rotation policy remain application responsibilities.

For the complete production contract and operational guidance, see [Production usage](docs/production-usage.md) and the [Persistence contract](docs/persistence.md).

## Installation

The package targets `net10.0`:

```bash
dotnet add package ReliableWebhooks --version 1.0.0
```

or:

```xml
<PackageReference Include="ReliableWebhooks" Version="1.0.0" />
```

## Quick start

This minimal example uses the in-memory store so it can run without external infrastructure. Replace it with a conforming durable `IWebhookDeliveryStore` before using ReliableWebhooks in production.

```csharp
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReliableWebhooks;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IWebhookDeliveryStore, InMemoryWebhookDeliveryStore>();

ReliableWebhooksBuilder webhooks = builder.Services.AddReliableWebhooks(options =>
{
    options.Dispatcher.MaxConcurrency = 4;
});

webhooks.AddHostedDispatcher();

using IHost host = builder.Build();

IWebhookEnqueueService enqueue =
    host.Services.GetRequiredService<IWebhookEnqueueService>();

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

await enqueue.EnqueueAsync(message);
await host.RunAsync();
```

`AddHostedDispatcher()` is opt-in. Applications can instead resolve and run `WebhookDispatcher` directly when they need to own its lifecycle.

A runnable end-to-end application with signing, success, retries, dead-letter behavior, and diagnostics is available in [`samples/ReliableWebhooks.Sample`](samples/ReliableWebhooks.Sample).

## Delivery flow

1. Create a `WebhookMessage` with a stable ID, event type, destination, exact payload bytes, content type, and optional headers.
2. Enqueue it through `IWebhookEnqueueService` or the lower-level `IWebhookDeliveryStore`.
3. `WebhookDispatcher` claims only due work up to its available concurrency and receives expiring leases.
4. `WebhookHttpTransport` performs one request attempt and returns a transport-neutral result.
5. Success and permanent failure become terminal states; retryable failures are scheduled by `IWebhookRetryPolicy`.
6. A conforming durable store persists the resulting state so expired or abandoned work can be reclaimed safely.

Stable IDs make enqueueing idempotent at the store boundary, while leases prevent stale workers from overwriting the current owner. These mechanisms support at-least-once delivery; they do not remove the need for receiver idempotency.

## Minimal production registration

A production application owns the persistence adapter and explicitly registers it:

```csharp
services.AddSingleton<IWebhookDeliveryStore, MyDurableWebhookStore>();

ReliableWebhooksBuilder webhooks = services.AddReliableWebhooks();
webhooks.AddHostedDispatcher();
```

The store must implement the durability, atomic claim, lease, stale-owner rejection, retry scheduling, and terminal-state semantics defined by the persistence contract.

## Read next

- **Production usage:** [`docs/production-usage.md`](docs/production-usage.md) — DI/hosting, retry and response classification, signing and receiver verification, destination security, observability, configuration, tuning, and troubleshooting.
- **Persistence contract:** [`docs/persistence.md`](docs/persistence.md) — durable state, idempotent enqueue, atomic claims, leases, ownership, transitions, and conformance testing.
- **Runnable sample:** [`samples/ReliableWebhooks.Sample`](samples/ReliableWebhooks.Sample) — end-to-end integration using the public API.
- **v1.0.0 scope:** [`docs/release-v1.0.0.md`](docs/release-v1.0.0.md) — release boundaries, distribution, and supported surface.
- **Security policy:** [`SECURITY.md`](SECURITY.md) — supported security reporting process and project security guidance.

## Extensibility

The core behaviors are exposed through replaceable abstractions, including persistence, delivery transport, response classification, retry policy/jitter, request signing and secret resolution, destination policy, request-header resolution, dispatcher timing, and observability integration. The production guide documents the built-in defaults and the boundaries that custom implementations must preserve.

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for local development, validation, and contribution guidance.

## License

ReliableWebhooks is licensed under the [MIT License](LICENSE).
