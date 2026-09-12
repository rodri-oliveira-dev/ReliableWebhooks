# ReliableWebhooks

**English** | [Português (Brasil)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/README.pt-BR.md)

ReliableWebhooks is a .NET 10 library for building reliable outbound webhook delivery.

It provides composable primitives for stable webhook identities, persistence and lease coordination, bounded concurrent dispatching, one-attempt HTTP delivery, HMAC-SHA256 request signing, response classification, deterministic retry scheduling, and backend-neutral observability. The goal is to make the reliability concerns around webhook delivery explicit instead of hiding them inside an opaque background loop.

> **Status:** v0.1.0 is under development. The first public NuGet package has not been released yet. The current version provides the reliability primitives, concurrent dispatcher, signing, and observability described below; the built-in durable store and dependency-injection integration are still part of the work required before the first public release.

## Why ReliableWebhooks?

Sending an HTTP `POST` is simple. Delivering a webhook reliably is not.

Real applications must handle transient HTTP failures, network errors, process crashes, concurrent workers, retry storms, abandoned work, duplicate delivery, and servers that ask clients to retry later. A reliable solution also needs to preserve enough state to resume safely after a failure without pretending that exactly-once delivery is possible over HTTP.

ReliableWebhooks addresses those concerns by separating the delivery workflow into explicit responsibilities:

- a stable webhook message identity;
- a persistence contract for enqueueing, claiming, leasing, retrying, and completing deliveries;
- a concurrent dispatcher that claims only available capacity and coordinates graceful shutdown;
- an HTTP transport that performs exactly one request attempt per call;
- optional HMAC-SHA256 request signing over the exact outbound payload bytes;
- transport-neutral success, retryable-failure, and permanent-failure outcomes;
- a retry policy that calculates the next attempt without sleeping or blocking a worker;
- structured logs, traces, and metrics built on standard .NET diagnostics APIs;
- replaceable abstractions for persistence, transport, dispatch timing, signing, classification, and retry behavior.

## How it works

A ReliableWebhooks delivery is designed around a small state machine rather than a fire-and-forget HTTP call:

1. Create a `WebhookMessage` with a stable ID, event type, destination, exact payload bytes, content type, and optional headers.
2. Enqueue it through `IWebhookDeliveryStore`. Duplicate enqueue attempts with the same stable ID are deterministic. Wrap the store with `InstrumentedWebhookDeliveryStore` when enqueue logs and the queued metric are required independently of the persistence implementation.
3. `WebhookDispatcher` atomically claims due deliveries up to its available concurrency slots and receives expiring `WebhookDeliveryLease` instances.
4. `WebhookHttpTransport` performs one HTTP `POST` per claimed delivery; when a signer is configured, it signs the exact payload buffer used by the request and adds the delivery identity, event type, timestamp, and signature headers.
5. The transport returns a `WebhookDeliveryResult` with a transport-neutral outcome.
6. Successful and permanent failures are persisted immediately. Retryable failures are passed to `IWebhookRetryPolicy`, which returns either a future `NextAttemptAt` or a dead-letter decision.
7. The store records the resulting state so abandoned or failed work can be resumed safely. Lease ownership prevents stale workers from overwriting the current owner.
8. The lifecycle emits safe structured logs, an activity for each delivery attempt, and bounded metrics without requiring a telemetry backend.

When combined with a durable store, the intended delivery model is **at-least-once**, not exactly-once. Receivers must therefore be idempotent and tolerate duplicate deliveries.

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

IWebhookDeliveryStore store = new InstrumentedWebhookDeliveryStore(
    new InMemoryWebhookDeliveryStore());
await store.EnqueueAsync(message, DateTimeOffset.UtcNow);

using var handler = new HttpClientHandler
{
    AllowAutoRedirect = false,
};

using var httpClient = new HttpClient(handler);
IWebhookDeliveryTransport transport = new WebhookHttpTransport(httpClient);
IWebhookRetryPolicy retryPolicy = new DefaultWebhookRetryPolicy();

var dispatcher = new WebhookDispatcher(
    store,
    transport,
    retryPolicy,
    new WebhookDispatcherOptions
    {
        MaxConcurrency = 4,
        LeaseDuration = TimeSpan.FromMinutes(1),
        PollInterval = TimeSpan.FromSeconds(1),
        ShutdownGracePeriod = TimeSpan.FromSeconds(30),
    });

await dispatcher.RunAsync(stoppingToken);
```

`WebhookDispatcher` does not create unbounded work: it claims at most the number of currently available concurrency slots. On shutdown it stops new claims, lets in-flight deliveries finish during the configured grace period, and then cancels remaining attempts. Canceled work is not marked successful; its lease can expire and be reclaimed later.

`InMemoryWebhookDeliveryStore` is process-local and **not durable**. It exists for tests and samples only and must not be used as production persistence.

### Signing webhooks

Signing is enabled by supplying an `IWebhookRequestSigner`. The built-in `HmacSha256WebhookRequestSigner` resolves secret bytes through `IWebhookSigningSecretProvider`, so the library does not need to know whether secrets come from configuration, a secret manager, or another source.

```csharp
IWebhookSigningSecretProvider secretProvider = GetApplicationSecretProvider();
IWebhookRequestSigner signer = new HmacSha256WebhookRequestSigner(secretProvider);

var transport = new WebhookHttpTransport(
    httpClient,
    classifier: null,
    options: null,
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

## Observability

ReliableWebhooks emits telemetry through standard .NET APIs and does **not** depend on the OpenTelemetry SDK, a collector, or any exporter. Applications decide whether telemetry is collected and where it is sent.

The public `ReliableWebhooksInstrumentation` class exposes the canonical names used by the library:

```csharp
ReliableWebhooksInstrumentation.ActivitySourceName // "ReliableWebhooks"
ReliableWebhooksInstrumentation.MeterName          // "ReliableWebhooks"
```

The same class exposes stable names for the delivery-attempt activity and the published metrics, so integrations do not need to duplicate instrumentation strings.

### Structured logs

`WebhookDispatcher` has an additive constructor overload that accepts `ILogger`. Existing constructors remain valid and use a no-op logger. Enqueue telemetry is provided by `InstrumentedWebhookDeliveryStore`, a decorator that can wrap any `IWebhookDeliveryStore`, including a custom durable production store.

```csharp
ILogger logger = loggerFactory.CreateLogger("ReliableWebhooks");

IWebhookDeliveryStore durableStore = GetApplicationWebhookStore();
IWebhookDeliveryStore store = new InstrumentedWebhookDeliveryStore(
    durableStore,
    logger);

var dispatcher = new WebhookDispatcher(
    store,
    transport,
    retryPolicy,
    options,
    delay: null,
    logger);
```

`InstrumentedWebhookDeliveryStore` emits the enqueue event and queued metric only when a new delivery is actually persisted. Duplicate idempotent enqueue calls do not double-count queued deliveries. Claim telemetry is emitted by `WebhookDispatcher`, so the same claim is logged exactly once and custom stores receive the same lifecycle coverage.

The lifecycle uses stable event IDs for enqueue, claim, attempt start, success, retry scheduling, permanent failure, dead letter, cancellation, lease loss, and unexpected failure. Log properties are structured and deliberately exclude payload bodies, destination URLs/query strings, signing secrets, and signatures.

### Traces

Each dispatcher delivery attempt creates an activity named `ReliableWebhooks.DeliveryAttempt` from the `ReliableWebhooks` activity source. Safe attributes include:

- `webhook.id` for log/trace correlation;
- `webhook.event_type`;
- `webhook.attempt`;
- `webhook.outcome`;
- `http.response.status_code` when an HTTP response exists;
- `error.type` for unexpected exception types.

Payloads, secrets, signatures, and destination URLs are never added automatically. Webhook IDs are allowed in traces for correlation but are intentionally excluded from metric dimensions.

### Metrics

| Instrument | Type | Tags |
| --- | --- | --- |
| `reliablewebhooks.delivery.queued` | Counter | `webhook.event_type` |
| `reliablewebhooks.delivery.attempted` | Counter | `webhook.event_type` |
| `reliablewebhooks.delivery.succeeded` | Counter | `webhook.event_type` |
| `reliablewebhooks.delivery.retried` | Counter | `webhook.event_type` |
| `reliablewebhooks.delivery.permanently_failed` | Counter | `webhook.event_type` |
| `reliablewebhooks.delivery.dead_lettered` | Counter | `webhook.event_type` |
| `reliablewebhooks.delivery.duration` | Histogram in milliseconds | `webhook.event_type`, `webhook.outcome` |

`webhook.event_type` should come from a bounded application-defined vocabulary. Never encode customer IDs, request IDs, URLs, or other unbounded values into event types. Webhook IDs and arbitrary destinations are not metric tags.

### OpenTelemetry integration

A consumer can opt into OpenTelemetry with its own OpenTelemetry packages and exporter configuration. ReliableWebhooks itself does not require them:

```csharp
builder.Services
    .AddOpenTelemetry()
    .WithTracing(tracing =>
        tracing.AddSource(ReliableWebhooksInstrumentation.ActivitySourceName))
    .WithMetrics(metrics =>
        metrics.AddMeter(ReliableWebhooksInstrumentation.MeterName));
```

The application can then add OTLP, Azure Monitor, Prometheus, Grafana/Tempo, Datadog, Dynatrace, or another supported exporter/backend without changing ReliableWebhooks. Logging continues through standard `ILogger` and can be connected to the application's chosen logging/OpenTelemetry pipeline independently.

## Default behavior

| Area | Default |
| --- | --- |
| Dispatcher maximum concurrency | 4 |
| Dispatcher lease duration | 1 minute |
| Dispatcher poll interval | 1 second |
| Dispatcher shutdown grace period | 30 seconds |
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
- **Dispatcher concurrency is bounded.** Claims are limited to currently available slots instead of creating unbounded background tasks.
- **Shutdown is two-phase.** New claims stop first; in-flight work can finish within the grace period before remaining attempts are canceled.
- **One transport call means one HTTP attempt.** Retry loops are intentionally outside the transport.
- **Signed payloads use exact request bytes.** Receivers must verify the raw body rather than a parsed/reserialized representation.
- **Retry policies schedule; they do not wait.** `DefaultWebhookRetryPolicy` returns a future timestamp or dead-letter decision and never calls `Task.Delay`.
- **Telemetry is backend-neutral.** Logs, traces, and metrics use standard .NET APIs; exporters remain an application concern.
- **Persistence is replaceable.** The core package does not depend on a specific database provider.

The current development version does not yet include the production durable EF Core store or dependency-injection/hosted-service integration planned for v0.1.0.

## Extensibility

The main behaviors are exposed through public abstractions:

- `IWebhookDeliveryStore` — persistence and lease coordination;
- `InstrumentedWebhookDeliveryStore` — persistence decorator that adds enqueue logs and the queued metric to any store implementation;
- `IWebhookDeliveryTransport` — one-attempt delivery transport used by the dispatcher;
- `IWebhookDispatcherDelay` — replaceable polling/grace-period timing for deterministic tests or custom scheduling;
- `IWebhookHttpResponseClassifier` — HTTP response classification;
- `IWebhookRetryPolicy` — retry and dead-letter decisions;
- `IWebhookRetryJitterSource` — deterministic or custom jitter generation;
- `IWebhookRequestSigner` — request-signing strategy;
- `IWebhookSigningSecretProvider` — per-message signing secret resolution;
- `ReliableWebhooksInstrumentation` — stable public diagnostics names for tracing and metrics integration.

`WebhookDispatcherOptions` also exposes `MaxConcurrency`, `LeaseDuration`, `PollInterval`, `ShutdownGracePeriod`, and `TimeProvider` so concurrency and timing remain configurable and testable.

This keeps persistence, dispatching, transport behavior, signing, retry strategy, and telemetry backend selection independently replaceable and testable.

## Support and contribution

Use [GitHub Issues](https://github.com/rodri-oliveira-dev/ReliableWebhooks/issues) for bugs, questions, and feature discussions.

For security issues, follow [SECURITY.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/SECURITY.md) instead of opening a public issue.

Contributions are welcome. See [CONTRIBUTING.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CONTRIBUTING.md) for the contribution workflow and [CHANGELOG.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CHANGELOG.md) for notable changes.

ReliableWebhooks is licensed under the [MIT License](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/LICENSE).
