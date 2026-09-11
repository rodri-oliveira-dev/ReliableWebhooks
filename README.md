# ReliableWebhooks

**English** | [Português (Brasil)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/README.pt-BR.md)

ReliableWebhooks is a .NET 10 library for building reliable outbound webhook delivery.

It provides composable primitives for stable webhook identities, persistence and lease coordination, one-attempt HTTP delivery, response classification, and deterministic retry scheduling. The goal is to make the reliability concerns around webhook delivery explicit instead of hiding them inside an opaque background loop.

> **Status:** v0.1.0 is under development. The first public NuGet package has not been released yet. The current version provides the reliability primitives described below; the built-in durable store and concurrent dispatcher are still part of the work required before the first public release.

## Why ReliableWebhooks?

Sending an HTTP `POST` is simple. Delivering a webhook reliably is not.

Real applications must handle transient HTTP failures, network errors, process crashes, concurrent workers, retry storms, abandoned work, duplicate delivery, and servers that ask clients to retry later. A reliable solution also needs to preserve enough state to resume safely after a failure without pretending that exactly-once delivery is possible over HTTP.

ReliableWebhooks addresses those concerns by separating the delivery workflow into explicit responsibilities:

- a stable webhook message identity;
- a persistence contract for enqueueing, claiming, leasing, retrying, and completing deliveries;
- an HTTP transport that performs exactly one request attempt per call;
- transport-neutral success, retryable-failure, and permanent-failure outcomes;
- a retry policy that calculates the next attempt without sleeping or blocking a worker;
- replaceable abstractions for persistence, classification, and retry behavior.

## How it works

A ReliableWebhooks delivery is designed around a small state machine rather than a fire-and-forget HTTP call:

1. Create a `WebhookMessage` with a stable ID, event type, destination, exact payload bytes, content type, and optional headers.
2. Enqueue it through `IWebhookDeliveryStore`. Duplicate enqueue attempts with the same stable ID are deterministic.
3. A worker atomically claims due deliveries and receives an expiring `WebhookDeliveryLease`.
4. `WebhookHttpTransport` performs one HTTP `POST` and returns a `WebhookDeliveryResult`.
5. Successful and permanent failures can be persisted immediately. Retryable failures are passed to `IWebhookRetryPolicy`, which returns either a future `NextAttemptAt` or a dead-letter decision.
6. The store records the resulting state so abandoned or failed work can be resumed safely.

When combined with a durable store and dispatcher, the intended delivery model is **at-least-once**, not exactly-once. Receivers must therefore be idempotent and tolerate duplicate deliveries.

## Installation

The planned NuGet package ID is `ReliableWebhooks` and the library targets `net10.0`.

The first public package has not been published yet. After the v0.1.0 release, installation will be:

```bash
dotnet add package ReliableWebhooks --version 0.1.0
```

or:

```xml
<PackageReference Include="ReliableWebhooks" Version="0.1.0" />
```

## Quick start

The current API exposes the delivery primitives directly. The example below uses the in-memory store only to demonstrate the workflow.

```csharp
using System;
using System.Linq;
using System.Net.Http;
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
DateTimeOffset now = DateTimeOffset.UtcNow;

await store.EnqueueAsync(message, now);

WebhookDeliveryLease lease = (await store.ClaimDueAsync(
    now,
    leaseDuration: TimeSpan.FromSeconds(30),
    maxCount: 1)).Single();

using var handler = new HttpClientHandler
{
    AllowAutoRedirect = false,
};

using var httpClient = new HttpClient(handler);
var transport = new WebhookHttpTransport(httpClient);

WebhookDeliveryResult result = await transport.SendAsync(lease.Delivery.Message);
```

`InMemoryWebhookDeliveryStore` is process-local and **not durable**. It exists for tests and samples only and must not be used as production persistence.

### Handling the result

The transport deliberately does not retry. It reports one attempt and leaves the state transition to the caller or dispatcher.

```csharp
DateTimeOffset completedAt = DateTimeOffset.UtcNow;

switch (result.Outcome)
{
    case WebhookDeliveryOutcome.Success:
        await store.MarkSucceededAsync(lease, completedAt);
        break;

    case WebhookDeliveryOutcome.PermanentFailure:
        await store.MarkPermanentlyFailedAsync(
            lease,
            completedAt,
            lastError: null);
        break;

    case WebhookDeliveryOutcome.RetryableFailure:
        var retryPolicy = new DefaultWebhookRetryPolicy();
        WebhookRetryDecision decision = retryPolicy.GetDecision(
            WebhookRetryContext.FromResult(
                lease.Delivery,
                result,
                completedAt));

        if (decision.ShouldRetry)
        {
            await store.ScheduleRetryAsync(
                lease,
                completedAt,
                decision.NextAttemptAt!.Value,
                lastError: null);
        }
        else
        {
            await store.DeadLetterAsync(
                lease,
                completedAt,
                lastError: null);
        }

        break;
}
```

## Default behavior

| Area | Default |
| --- | --- |
| HTTP attempt timeout | 30 seconds |
| Captured response body | Up to 16 KiB |
| Success classification | Any `2xx` response |
| Retryable HTTP responses | `408`, `425`, `429`, and `5xx` |
| Permanent HTTP responses | Other status codes, including redirects |
| Maximum attempts | 5, including the current attempt |
| Base retry delay | 1 second |
| Maximum retry delay | 5 minutes |
| Jitter | 0 to 20% positive jitter before the maximum-delay cap |
| `Retry-After` | Honored when it schedules later than the local retry delay, capped by the configured maximum delay |

Automatic redirects must be disabled on the `HttpClient` handler so a single transport invocation cannot silently become multiple HTTP requests or change the request method.

Malformed `Retry-After` values are ignored. Valid delta-seconds and HTTP-date values are exposed through `WebhookDeliveryResult` and consumed by the default retry policy.

## Delivery guarantees and boundaries

ReliableWebhooks is designed around explicit delivery semantics:

- **At-least-once, not exactly-once.** Duplicate delivery can occur, especially when a worker fails after sending but before persisting the result.
- **Stable IDs support duplicate-safe enqueueing.** Receivers still need application-level idempotency.
- **Leases coordinate active ownership.** While a lease is valid, two workers should not own the same delivery simultaneously; expired work can be reclaimed.
- **One transport call means one HTTP attempt.** Retry loops are intentionally outside the transport.
- **Retry policies schedule; they do not wait.** `DefaultWebhookRetryPolicy` returns a future timestamp or dead-letter decision and never calls `Task.Delay`.
- **Persistence is replaceable.** The core package does not depend on a specific database provider.

The current development version does not yet include the production durable EF Core store, concurrent dispatcher, HMAC signing, dependency-injection integration, or observability planned for v0.1.0.

## Extensibility

The main behaviors are exposed through public abstractions:

- `IWebhookDeliveryStore` — persistence and lease coordination;
- `IWebhookHttpResponseClassifier` — HTTP response classification;
- `IWebhookRetryPolicy` — retry and dead-letter decisions;
- `IWebhookRetryJitterSource` — deterministic or custom jitter generation.

This keeps persistence, transport behavior, and retry strategy independently replaceable and testable.

## Support and contribution

Use [GitHub Issues](https://github.com/rodri-oliveira-dev/ReliableWebhooks/issues) for bugs, questions, and feature discussions.

For security issues, follow [SECURITY.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/SECURITY.md) instead of opening a public issue.

Contributions are welcome. See [CONTRIBUTING.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CONTRIBUTING.md) for the contribution workflow and [CHANGELOG.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CHANGELOG.md) for notable changes.

ReliableWebhooks is licensed under the [MIT License](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/LICENSE).
