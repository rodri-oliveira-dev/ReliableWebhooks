# ReliableWebhooks

**English** | [Português (Brasil)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/README.pt-BR.md)

ReliableWebhooks is a .NET 10 library for building reliable outbound webhook delivery.

It provides composable primitives for stable webhook identities, persistence and lease coordination, one-attempt HTTP delivery, HMAC-SHA256 request signing, response classification, and deterministic retry scheduling. The goal is to make the reliability concerns around webhook delivery explicit instead of hiding them inside an opaque background loop.

> **Status:** v0.1.0 is under development. The first public NuGet package has not been released yet. The current version provides the reliability primitives described below; the built-in durable store and concurrent dispatcher are still part of the work required before the first public release.

## Why ReliableWebhooks?

Sending an HTTP `POST` is simple. Delivering a webhook reliably is not.

Real applications must handle transient HTTP failures, network errors, process crashes, concurrent workers, retry storms, abandoned work, duplicate delivery, and servers that ask clients to retry later. A reliable solution also needs to preserve enough state to resume safely after a failure without pretending that exactly-once delivery is possible over HTTP.

ReliableWebhooks addresses those concerns by separating the delivery workflow into explicit responsibilities:

- a stable webhook message identity;
- a persistence contract for enqueueing, claiming, leasing, retrying, and completing deliveries;
- an HTTP transport that performs exactly one request attempt per call;
- optional HMAC-SHA256 request signing over the exact outbound payload bytes;
- transport-neutral success, retryable-failure, and permanent-failure outcomes;
- a retry policy that calculates the next attempt without sleeping or blocking a worker;
- replaceable abstractions for persistence, signing, classification, and retry behavior.

## How it works

A ReliableWebhooks delivery is designed around a small state machine rather than a fire-and-forget HTTP call:

1. Create a `WebhookMessage` with a stable ID, event type, destination, exact payload bytes, content type, and optional headers.
2. Enqueue it through `IWebhookDeliveryStore`. Duplicate enqueue attempts with the same stable ID are deterministic.
3. A worker atomically claims due deliveries and receives an expiring `WebhookDeliveryLease`.
4. `WebhookHttpTransport` performs one HTTP `POST`; when a signer is configured, it signs the exact payload buffer used by the request and adds the delivery identity, event type, timestamp, and signature headers.
5. The transport returns a `WebhookDeliveryResult` with a transport-neutral outcome.
6. Successful and permanent failures can be persisted immediately. Retryable failures are passed to `IWebhookRetryPolicy`, which returns either a future `NextAttemptAt` or a dead-letter decision.
7. The store records the resulting state so abandoned or failed work can be resumed safely.

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
    leaseDuration: TimeSpan.FromMinutes(1),
    maxCount: 1)).Single();

using var handler = new HttpClientHandler
{
    AllowAutoRedirect = false,
};

using var httpClient = new HttpClient(handler);
var transport = new WebhookHttpTransport(httpClient);

WebhookDeliveryResult result = await transport.SendAsync(lease.Delivery.Message);
```

The one-minute lease intentionally exceeds the transport's default 30-second attempt timeout, leaving time to persist the result while ownership is still valid. Production workers should size leases beyond their complete attempt budget or renew active leases when processing can run longer.

`InMemoryWebhookDeliveryStore` is process-local and **not durable**. It exists for tests and samples only and must not be used as production persistence.

### Signing webhooks

Signing is enabled by supplying an `IWebhookRequestSigner`. The built-in `HmacSha256WebhookRequestSigner` resolves secret bytes through `IWebhookSigningSecretProvider`, so the library does not need to know whether secrets come from configuration, a secret manager, or another source.

```csharp
IWebhookSigningSecretProvider secretProvider = GetApplicationSecretProvider();
IWebhookRequestSigner signer = new HmacSha256WebhookRequestSigner(secretProvider);

var transport = new WebhookHttpTransport(
    httpClient,
    signer: signer);
```

With the default `WebhookSigningOptions`, each signed request contains:

- `X-Webhook-Id`: the stable `WebhookMessage.Id`;
- `X-Webhook-Event`: the `WebhookMessage.EventType`;
- `X-Webhook-Timestamp`: the UTC Unix timestamp in seconds;
- `X-Webhook-Signature`: `v1=<lowercase HMAC-SHA256 hex digest>`.

Header names can be customized through `WebhookHttpTransportOptions.Signing`. Generated signing headers take precedence over custom message headers with the same names.

The canonical HMAC input is:

```text
UTF8(unixTimestampSeconds + ".") || exactRequestPayloadBytes
```

The payload bytes are not reserialized or normalized before signing. The same byte array is used both for HMAC calculation and for the HTTP request content.

A receiver can verify a delivery independently by:

1. reading the timestamp and signature headers without modifying the request body;
2. parsing the timestamp as Unix seconds and rejecting timestamps outside the receiver's replay-tolerance window;
3. rebuilding the canonical bytes as UTF-8 `timestamp + "."` followed by the raw request body bytes;
4. calculating HMAC-SHA256 with the shared secret;
5. encoding the digest as lowercase hexadecimal and prefixing it with `v1=`;
6. comparing the calculated signature with the received signature using a constant-time comparison.

Signing secrets are never included in library-generated exception messages or automatic telemetry. Applications should follow the same rule in custom secret providers and signers.

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
| Signing algorithm | HMAC-SHA256 when a signer is configured |
| Signing canonical format | `UTF8(unixTimestamp + ".") || payload bytes` |
| Signing headers | `X-Webhook-Id`, `X-Webhook-Event`, `X-Webhook-Timestamp`, `X-Webhook-Signature` |
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
- **Signed payloads use exact request bytes.** Receivers must verify the raw body rather than a parsed/reserialized representation.
- **Retry policies schedule; they do not wait.** `DefaultWebhookRetryPolicy` returns a future timestamp or dead-letter decision and never calls `Task.Delay`.
- **Persistence is replaceable.** The core package does not depend on a specific database provider.

The current development version does not yet include the production durable EF Core store, concurrent dispatcher, dependency-injection integration, or observability planned for v0.1.0.

## Extensibility

The main behaviors are exposed through public abstractions:

- `IWebhookDeliveryStore` — persistence and lease coordination;
- `IWebhookHttpResponseClassifier` — HTTP response classification;
- `IWebhookRetryPolicy` — retry and dead-letter decisions;
- `IWebhookRetryJitterSource` — deterministic or custom jitter generation;
- `IWebhookRequestSigner` — request-signing strategy;
- `IWebhookSigningSecretProvider` — per-message signing secret resolution.

This keeps persistence, transport behavior, signing, and retry strategy independently replaceable and testable.

## Support and contribution

Use [GitHub Issues](https://github.com/rodri-oliveira-dev/ReliableWebhooks/issues) for bugs, questions, and feature discussions.

For security issues, follow [SECURITY.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/SECURITY.md) instead of opening a public issue.

Contributions are welcome. See [CONTRIBUTING.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CONTRIBUTING.md) for the contribution workflow and [CHANGELOG.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CHANGELOG.md) for notable changes.

ReliableWebhooks is licensed under the [MIT License](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/LICENSE).
