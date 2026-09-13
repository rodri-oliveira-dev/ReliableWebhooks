# ReliableWebhooks

[![CI](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/ci.yml)
[![Release](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml/badge.svg)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/ReliableWebhooks.svg)](https://www.nuget.org/packages/ReliableWebhooks/)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

**English** | [Português (Brasil)](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/README.pt-BR.md)

ReliableWebhooks is a .NET 10 library for building reliable outbound webhook delivery.

It provides composable primitives for stable webhook identities, persistence and lease coordination, bounded concurrent dispatching, one-attempt HTTP delivery, HMAC-SHA256 request signing, response classification, deterministic retry scheduling, backend-neutral observability, and standard .NET dependency-injection/hosting integration. The goal is to make the reliability concerns around webhook delivery explicit instead of hiding them inside an opaque background loop.

> **v1.0.0 scope:** the first public release provides the reliable-delivery engine, technology-agnostic `IWebhookDeliveryStore` contract, retries, leasing, signing, observability, dependency injection, hosted dispatching, and production guidance. Production restart durability is supplied by a consumer-provided conforming durable store.

## Why ReliableWebhooks?

Sending an HTTP `POST` is simple. Delivering a webhook reliably is not.

Real applications must handle transient HTTP failures, network errors, process crashes, concurrent workers, retry storms, abandoned work, duplicate delivery, and servers that ask clients to retry later. A reliable solution also needs to preserve enough state to resume safely after a failure without pretending that exactly-once delivery is possible over HTTP.

ReliableWebhooks addresses those concerns by separating the delivery workflow into explicit responsibilities:

- a stable webhook message identity;
- a persistence contract for enqueueing, claiming, leasing, retrying, and completing deliveries;
- a concurrent dispatcher that claims only available capacity and coordinates graceful shutdown;
- an HTTP transport that performs exactly one request attempt per call;
- optional HMAC-SHA256 request signing over generated metadata and the exact outbound payload bytes;
- transport-neutral success, retryable-failure, and permanent-failure outcomes;
- a retry policy that calculates the next attempt without sleeping or blocking a worker;
- structured logs, traces, and metrics built on standard .NET diagnostics APIs;
- standard Microsoft DI and Generic Host integration with an optional hosted dispatcher;
- replaceable abstractions for persistence, transport, dispatch timing, signing, classification, and retry behavior.

## How it works

A ReliableWebhooks delivery is designed around a small state machine rather than a fire-and-forget HTTP call:

1. Create a `WebhookMessage` with a stable ID, event type, destination, exact payload bytes, content type, and optional headers.
2. Enqueue it through `IWebhookDeliveryStore` or the application-facing `IWebhookEnqueueService`. Duplicate enqueue attempts with the same stable ID are deterministic. Wrap the store with `InstrumentedWebhookDeliveryStore` when enqueue logs and the queued metric are required independently of the persistence implementation.
3. `WebhookDispatcher` atomically claims due deliveries up to its available concurrency slots and receives expiring `WebhookDeliveryLease` instances.
4. `WebhookHttpTransport` performs one HTTP `POST` per claimed delivery; when a signer is configured, it signs the exact payload buffer used by the request and adds the delivery identity, event type, timestamp, and signature headers.
5. The transport returns a `WebhookDeliveryResult` with a transport-neutral outcome.
6. Successful and permanent failures are persisted immediately. Retryable failures are passed to `IWebhookRetryPolicy`, which returns either a future `NextAttemptAt` or a dead-letter decision.
7. The store records the resulting state so abandoned or failed work can be resumed safely. Lease ownership prevents stale workers from overwriting the current owner.
8. The lifecycle emits safe structured logs, an activity for each delivery attempt, and bounded metrics without requiring a telemetry backend.

When combined with a durable store, the intended delivery model is **at-least-once**, not exactly-once. Receivers must therefore be idempotent and tolerate duplicate deliveries.

## Installation

The NuGet package ID is `ReliableWebhooks` and the library targets `net10.0`. Install v1.0.0 with:

```bash
dotnet add package ReliableWebhooks --version 1.0.0
```

or:

```xml
<PackageReference Include="ReliableWebhooks" Version="1.0.0" />
```

## v1.0.0 boundaries

The first public release intentionally keeps persistence technology-agnostic:

- no production durable store is bundled; applications register a conforming `IWebhookDeliveryStore`;
- `InMemoryWebhookDeliveryStore` is non-durable and intended only for tests, samples, and local development;
- delivery is at-least-once when backed by a conforming durable store, so receivers must be idempotent;
- exactly-once delivery, receiver-side idempotency, automatic dead-letter replay, and secret-rotation policy are not guaranteed;
- EF Core, Dapper, ADO.NET, Redis, files, document databases, and other persistence technologies are optional consumer choices, not core dependencies.

See [`docs/production-usage.md`](docs/production-usage.md) for production integration and [`docs/release-v1.0.0.md`](docs/release-v1.0.0.md) for release/distribution details.

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
    UseCookies = false,
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

For the full persistence contract and conformance guidance, see [`docs/persistence.md`](docs/persistence.md).

A runnable end-to-end sample is available in [`samples/ReliableWebhooks.Sample`](samples/ReliableWebhooks.Sample). For production integration, configuration, receiver idempotency, signing verification, and troubleshooting, see [`docs/production-usage.md`](docs/production-usage.md).

### Dependency injection and hosted dispatcher

Applications using the .NET Generic Host can register the standard integration through `AddReliableWebhooks`. A production application must register its own `IWebhookDeliveryStore`; ReliableWebhooks intentionally does not select the non-durable in-memory store as a production default.

```csharp
services.AddSingleton<IWebhookDeliveryStore, MyDurableWebhookStore>();

ReliableWebhooksBuilder webhooks = services.AddReliableWebhooks(options =>
{
    options.Dispatcher.MaxConcurrency = 8;
    options.Dispatcher.LeaseDuration = TimeSpan.FromMinutes(2);
    options.Dispatcher.PollInterval = TimeSpan.FromSeconds(1);
    options.MessageLimits.MaxPayloadBytes = 1024 * 1024;
    options.Transport = new WebhookHttpTransportOptions
    {
        AttemptTimeout = TimeSpan.FromSeconds(30),
    };
});

webhooks.AddHostedDispatcher();
```

`AddHostedDispatcher()` is opt-in. Without it, applications can resolve and run `WebhookDispatcher` themselves. The default transport uses `IHttpClientFactory`, requires HTTPS destinations, disables automatic redirects and cookies, removes the default `HttpClientFactory` request loggers to avoid leaking secret-bearing destination paths or query strings, and sets `HttpClient.Timeout` to infinite so `WebhookHttpTransportOptions.AttemptTimeout` remains the authoritative per-attempt timeout. Additional handlers or client configuration can be added through `ReliableWebhooksBuilder.HttpClientBuilder`. Direct `WebhookHttpTransport` construction uses the caller-provided `HttpClient` as configured, including any handler cookie policy.

ReliableWebhooks treats destinations as operator-trusted by default after validating that they are absolute HTTP/HTTPS URIs, but the transport rejects plaintext `http://` unless `WebhookHttpTransportOptions.AllowInsecureHttp` is explicitly set. Use that opt-in only for development, loopback, or a deliberately trusted plaintext network. HMAC signing does not provide confidentiality or TLS server authentication. Applications that accept tenant-provided or otherwise untrusted webhook URLs should also configure `WebhookHttpTransportOptions.DestinationPolicy`, for example with `new PublicNetworkWebhookDestinationPolicy(allowedHosts: ["internal-webhooks.example"])`. That policy resolves DNS names before each attempt and denies loopback, unspecified, multicast, link-local, private, shared carrier-grade, documentation, benchmarking, transition, reserved, and other special-use targets for IPv4 and IPv6 unless a host is explicitly allow-listed. A deterministic destination denial is a permanent delivery failure and is not retried. With a standard `HttpClient`, validation happens before `SendAsync`; keep automatic redirects disabled and use an exact allow-list for any intended intranet destinations to avoid broad SSRF bypasses.

The default response classifier, retry policy, transport, and dispatcher are registered with replaceable DI registrations. Stores and signers are application-provided, and dispatcher timing or retry jitter abstractions can also be replaced. Invalid dispatcher, retry, transport, or signing options are validated when options are resolved and by Generic Host startup validation.

Application code can enqueue without depending directly on persistence scheduling details:

```csharp
IWebhookEnqueueService webhookEnqueue =
    serviceProvider.GetRequiredService<IWebhookEnqueueService>();

await webhookEnqueue.EnqueueAsync(message, cancellationToken);
```

`IWebhookEnqueueService` enforces `ReliableWebhooksOptions.MessageLimits` before a delivery is persisted. Defaults allow a 1 MiB payload, 32 persisted custom headers, 16 KiB of aggregate custom header name/value bytes, 128-character webhook IDs, 128-character event types, 256-character content types, and 2048-character destination URIs. Increase these limits only for receivers and tenants that are expected to need larger messages, and pair them with application-level quotas so one tenant cannot consume the whole queue. Code that bypasses this service and calls `IWebhookDeliveryStore.EnqueueAsync` directly is also bypassing the standard production limit gate; call `WebhookMessageLimits.Validate(message)` explicitly in that path or enforce equivalent limits at the application boundary.

The integration depends only on `Microsoft.Extensions.*`; it does not require ASP.NET Core.

### Signing webhooks

Signing is enabled by supplying an `IWebhookRequestSigner`. The built-in `HmacSha256WebhookRequestSigner` resolves secret bytes through `IWebhookSigningSecretProvider`, so the library does not need to know whether secrets come from configuration, a secret manager, or another source. It requires at least 32 bytes (256 bits) of cryptographically generated HMAC key material and rejects empty or shorter secrets before signing. Generate these bytes with a CSPRNG and store them in a secret manager; do not use passwords, passphrases, tenant names, or other low-entropy strings as signing secrets.

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
- `Content-Type`: the normalized `WebhookMessage.ContentType`;
- `X-Webhook-Timestamp`: the UTC Unix timestamp in seconds;
- `X-Webhook-Signature`: `v1=<lowercase HMAC-SHA256 hex digest>`.

Header names can be customized through `WebhookHttpTransportOptions.Signing`. Generated signing headers take precedence over custom message headers with the same names.

`WebhookMessage.Id` and `WebhookMessage.EventType` must be non-empty and must not contain control characters such as CR, LF, or NUL because they are used in generated headers and telemetry. `WebhookMessage.ContentType` must be a syntactically valid HTTP media type, including vendor media types such as `application/vnd.example+json`.

Custom headers supplied to `WebhookMessage` must use valid HTTP token names, are compared case-insensitively for duplicates, and must not contain control characters such as CR, LF, or NUL in their values. The default transport reserves routing and framing headers that message data must not control: `Host`, `Content-Length`, `Transfer-Encoding`, `Connection`, `TE`, `Trailer`, `Upgrade`, `Expect`, `Keep-Alive`, `Proxy-Authenticate`, `Proxy-Authorization`, and `Proxy-Connection`. Sensitive credential headers such as `Authorization` and `Cookie` are not accepted in the persisted message; resolve them at send time through `IWebhookRequestHeaderProvider` so queued attempts pick up rotation without rewriting stored deliveries. Treat custom header values as sensitive whenever they come from tenants, subscribers, or other external configuration. Advanced users who need lower-level HTTP control should provide a custom `IWebhookDeliveryTransport`.

The `v1` canonical HMAC input is versioned and length-prefixed:

```text
ASCII("rw-hmac-sha256/v1\0")
|| frame(UTF8(unixTimestampSeconds))
|| frame(UTF8(webhookId))
|| frame(UTF8(eventType))
|| frame(UTF8(contentType))
|| frame(exactRequestPayloadBytes)
```

Each `frame(value)` is an eight-byte big-endian length followed by the exact value bytes. The payload bytes are not reserialized or normalized before signing. The same byte array is used both for HMAC calculation and for the HTTP request content. The content type is normalized with the platform HTTP media-type parser when the `WebhookMessage` is created, so the authenticated content-type frame matches the serialized `Content-Type` header sent by the default transport. The authenticated values are the timestamp, webhook ID, event type, content type, and body bytes. Custom headers, destination URI, and the configurable signing header names are not part of the built-in HMAC envelope.

A receiver can verify a delivery independently by:

1. reading the generated ID, event, timestamp, signature, and content type without modifying the request body;
2. parsing the timestamp as Unix seconds and rejecting timestamps outside the receiver's replay-tolerance window;
3. rebuilding the `rw-hmac-sha256/v1` canonical frames with those metadata values and the raw request body bytes;
4. calculating HMAC-SHA256 with the shared secret;
5. encoding the digest as lowercase hexadecimal and prefixing it with `v1=`;
6. comparing the calculated signature with the received signature using a constant-time comparison.

Signing secrets are never included in library-generated exception messages or automatic telemetry. Applications should follow the same rule in custom secret providers and signers. Rotate secrets through your application secret provider and receiver configuration, and use different secrets for distinct trust audiences or receivers so one receiver compromise does not allow signatures to be forged for another.

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
| `reliablewebhooks.delivery.queued` | Counter | None by default |
| `reliablewebhooks.delivery.attempted` | Counter | None by default |
| `reliablewebhooks.delivery.succeeded` | Counter | None by default |
| `reliablewebhooks.delivery.retried` | Counter | None by default |
| `reliablewebhooks.delivery.permanently_failed` | Counter | None by default |
| `reliablewebhooks.delivery.dead_lettered` | Counter | None by default |
| `reliablewebhooks.delivery.duration` | Histogram in milliseconds | `webhook.outcome` |

Raw `WebhookMessage.EventType` is not used as a metric dimension by default. To opt into an event-type metric tag, configure `WebhookMetricsOptions.EventTypeTagAllowList` with a bounded vocabulary; values outside the allow-list are reported as `other`. Never include customer IDs, request IDs, destinations, webhook IDs, payload data, signatures, or other high-cardinality/sensitive values in metric dimensions.

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
| Outbound payload size | Up to 1 MiB through `IWebhookEnqueueService` |
| Persisted custom headers | Up to 32 headers and 16 KiB aggregate name/value bytes through `IWebhookEnqueueService` |
| Persisted metadata length | ID/event type up to 128 characters, content type up to 256, destination URI up to 2048 through `IWebhookEnqueueService` |
| Plain HTTP destinations | Rejected unless `AllowInsecureHttp = true` |
| Success classification | Any `2xx` response |
| Retryable HTTP responses | `408`, `425`, `429`, and `5xx` |
| Permanent HTTP responses | Other status codes, including redirects |
| Signing algorithm | HMAC-SHA256 when a signer is configured |
| Signing canonical format | `rw-hmac-sha256/v1` marker plus length-prefixed timestamp, webhook ID, event type, content type, and payload bytes |
| Signing headers | `X-Webhook-Id`, `X-Webhook-Event`, `X-Webhook-Timestamp`, `X-Webhook-Signature` |
| Maximum attempts | 5, including the current attempt |
| Base retry delay | 1 second |
| Maximum retry delay | 5 minutes |
| Jitter | 0 to 20% positive jitter before the maximum-delay cap |
| `Retry-After` | Honored when it schedules later than the local retry delay; `MaxDelay` caps only local backoff/jitter |

Automatic redirects and automatic cookies must be disabled on the `HttpClient` handler so a single transport invocation cannot silently become multiple HTTP requests or retain receiver-controlled state for later deliveries. The default DI-managed client configures this automatically.

Malformed `Retry-After` values are ignored. Valid delta-seconds and HTTP-date values are exposed through `WebhookDeliveryResult` and consumed by the default retry policy.

## Delivery guarantees and boundaries

ReliableWebhooks is designed around explicit delivery semantics:

- **At-least-once, not exactly-once.** Duplicate delivery can occur, especially when a worker fails after sending but before persisting the result.
- **Stable IDs support duplicate-safe enqueueing.** Receivers still need application-level idempotency.
- **Message resource limits protect the queue.** The standard enqueue service rejects oversized payloads, headers, and persisted metadata before store writes. Applications should still apply tenant quotas and business-level payload constraints.
- **Leases coordinate active ownership.** While a lease is valid, two workers should not own the same delivery simultaneously; expired work can be reclaimed.
- **Dispatcher concurrency is bounded.** Claims are limited to currently available slots instead of creating unbounded background tasks.
- **Shutdown is two-phase.** New claims stop first; in-flight work can finish within the grace period before remaining attempts are canceled.
- **One transport call means one HTTP attempt.** Retry loops are intentionally outside the transport.
- **Delivery-scoped extension failures are isolated.** Exceptions from transport/signing/classification for one delivery are logged by exception type and move through retry/dead-letter handling; store claim and state-transition failures remain infrastructure failures.
- **Signed payloads use exact request bytes.** Receivers must verify the raw body rather than a parsed/reserialized representation.
- **Retry policies schedule; they do not wait.** `DefaultWebhookRetryPolicy` returns a future timestamp or dead-letter decision and never calls `Task.Delay`.
- **Telemetry is backend-neutral.** Logs, traces, and metrics use standard .NET APIs; exporters remain an application concern.
- **Persistence is replaceable.** The core package does not depend on a specific database provider.

`ReliableWebhooks` v1.0.0 does not require a built-in production persistence adapter. Applications provide a conforming durable `IWebhookDeliveryStore`; optional EF Core, Dapper, Redis, file-backed, or other adapters may be introduced independently.

## Extensibility

The main behaviors are exposed through public abstractions:

- `IWebhookDeliveryStore` — persistence and lease coordination;
- `InstrumentedWebhookDeliveryStore` — persistence decorator that adds enqueue logs and the queued metric to any store implementation;
- `IWebhookEnqueueService` — application-facing enqueue API registered by the DI integration;
- `IWebhookDeliveryTransport` — one-attempt delivery transport used by the dispatcher;
- `IWebhookDispatcherDelay` — replaceable polling/grace-period timing for deterministic tests or custom scheduling;
- `IWebhookHttpResponseClassifier` — HTTP response classification;
- `IWebhookRetryPolicy` — retry and dead-letter decisions;
- `IWebhookRetryJitterSource` — deterministic or custom jitter generation;
- `IWebhookDestinationPolicy` — destination authorization for untrusted webhook URLs;
- `IWebhookRequestSigner` — request-signing strategy;
- `IWebhookSigningSecretProvider` — per-message signing secret resolution;
- `ReliableWebhooksInstrumentation` — stable public diagnostics names for tracing and metrics integration.

`ReliableWebhooksOptions` groups dispatcher, message limits, retry, transport, and signing configuration for DI consumers. `WebhookDispatcherOptions` exposes `MaxConcurrency`, `LeaseDuration`, `PollInterval`, `ShutdownGracePeriod`, and `TimeProvider` so concurrency and timing remain configurable and testable. `WebhookMessageLimits` exposes the standard per-message resource boundary used by `IWebhookEnqueueService` and can also be applied explicitly by applications that enqueue directly through a store.

This keeps persistence, dispatching, transport behavior, signing, retry strategy, hosting, and telemetry backend selection independently replaceable and testable.

## Support and contribution

Use [GitHub Issues](https://github.com/rodri-oliveira-dev/ReliableWebhooks/issues) for bugs, questions, and feature discussions.

For security issues, follow [SECURITY.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/SECURITY.md) instead of opening a public issue.

Contributions are welcome. See [CONTRIBUTING.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CONTRIBUTING.md) for the contribution workflow and [CHANGELOG.md](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/CHANGELOG.md) for notable changes.

ReliableWebhooks is licensed under the [MIT License](https://github.com/rodri-oliveira-dev/ReliableWebhooks/blob/main/LICENSE).
