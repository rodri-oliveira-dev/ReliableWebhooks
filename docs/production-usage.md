# Production usage guide

This guide describes how to integrate ReliableWebhooks into a production .NET application without coupling the library to a specific persistence technology.

For a runnable local example, see [`samples/ReliableWebhooks.Sample`](../samples/ReliableWebhooks.Sample). For the full persistence-port semantics and adapter guidance, see [`persistence.md`](persistence.md).

## Delivery model

ReliableWebhooks is designed for **at-least-once delivery**, not exactly-once delivery. A delivery can reach the receiver more than once when a worker sends the HTTP request successfully but fails before persisting the successful transition, or when a lease expires while ownership is uncertain.

Receivers must therefore be idempotent. Prefer a stable application identifier carried inside the signed payload. The default `X-Webhook-Id` header is useful for correlation, but it is not part of the default HMAC canonical bytes; if a receiver uses that header as its idempotency key, it should first verify the signature and cross-check the header against an identifier inside the signed payload.

At-least-once behavior across application restarts additionally requires a conforming **durable** `IWebhookDeliveryStore`. `InMemoryWebhookDeliveryStore` is process-local and exists only for tests, samples, and local experimentation.

## Register the library

A production application selects and registers its own persistence implementation before registering ReliableWebhooks:

```csharp
services.AddSingleton<IWebhookDeliveryStore, MyDurableWebhookStore>();

ReliableWebhooksBuilder webhooks = services.AddReliableWebhooks(options =>
{
    options.Dispatcher.MaxConcurrency = 8;
    options.Dispatcher.LeaseDuration = TimeSpan.FromMinutes(2);
    options.Dispatcher.PollInterval = TimeSpan.FromSeconds(1);
    options.Dispatcher.ShutdownGracePeriod = TimeSpan.FromSeconds(30);

    options.Retry = new WebhookRetryPolicyOptions
    {
        MaxAttempts = 5,
        BaseDelay = TimeSpan.FromSeconds(1),
        MaxDelay = TimeSpan.FromMinutes(5),
        JitterFactor = 0.2,
    };

    options.Transport = new WebhookHttpTransportOptions
    {
        AttemptTimeout = TimeSpan.FromSeconds(30),
        MaxResponseBodyBytes = 16 * 1024,
    };
});

webhooks.AddHostedDispatcher();
```

`AddHostedDispatcher()` is optional. When enabled, the Generic Host starts and stops the dispatcher with the application. Without it, the application can resolve `WebhookDispatcher` and control its lifetime directly.

`IWebhookDeliveryStore` may be registered as singleton, scoped, or transient. The DI integration resolves the store inside a short-lived operation scope for each enqueue, claim, renewal, or state-transition call, so singleton ReliableWebhooks services do not capture scoped adapters from the root provider. Scoped and transient adapters must coordinate through shared durable state; do not register a per-scope in-memory store and expect different operation scopes to see the same queue.

The core package does not require Entity Framework Core, Dapper, ADO.NET, Redis, files, a relational database, or a document database. Any implementation technology is valid when its `IWebhookDeliveryStore` behavior satisfies the contract.

## Enqueue from application code

Use `IWebhookEnqueueService` when application code does not need persistence scheduling details:

```csharp
byte[] payload = JsonSerializer.SerializeToUtf8Bytes(orderCreated);

WebhookMessage message = new(
    id: orderCreated.EventId,
    eventType: "order.created",
    destination: subscriber.WebhookUri,
    payload: payload,
    contentType: "application/json");

await webhookEnqueueService.EnqueueAsync(message, cancellationToken);
```

The ID must be stable for the logical webhook. Enqueueing the same ID again is idempotent and must return the existing delivery rather than create a second record.

## Production persistence responsibilities

`IWebhookDeliveryStore` is a persistence port. A conforming production adapter is responsible for providing the durability and concurrency semantics needed by the dispatcher:

- treat webhook IDs as opaque, case-sensitive identifiers;
- make enqueue idempotent and preserve the original stored message for duplicate IDs;
- claim due work atomically so one active delivery has at most one valid owner;
- issue non-empty lease tokens and persist lease expiration;
- allow expired work to be reclaimed with a new token and incremented attempt count;
- reject stale, expired, fabricated, or mismatched lease tokens with `WebhookDeliveryStoreConcurrencyException`;
- never shorten an existing lease during renewal;
- persist retry scheduling and clear active lease ownership;
- make `Succeeded`, `PermanentlyFailed`, and `DeadLettered` terminal and unclaimable;
- observe cancellation tokens and propagate genuine backend failures instead of translating them into business outcomes.

Adapters maintained with this repository should derive their tests from `WebhookDeliveryStoreConformanceTests`. External adapters should reproduce the same behavioral cases and add backend-specific durability, concurrency, schema/migration, and fault-injection tests.

A relational implementation might use transactions and conditional `UPDATE ... WHERE lease_token = ...` statements. A Redis implementation might use Lua scripts or transactions. A file-backed implementation might use atomic replacement and cross-process locking. These are implementation choices, not core-library requirements.

### Store implementation template

A production adapter should map the entire port to its chosen persistence mechanism rather than delegate reliability semantics back to application code. This skeleton shows the surface that an adapter owns without prescribing an ORM, database, cache, or file format:

```csharp
public sealed class MyDurableWebhookStore : IWebhookDeliveryStore
{
    public Task<WebhookEnqueueResult> EnqueueAsync(
        WebhookMessage message,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        // Persist once by the case-sensitive message ID and return the existing row on duplicates.
        throw new NotImplementedException();
    }

    public Task<IReadOnlyList<WebhookDeliveryLease>> ClaimDueAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        // Atomically select and lease at most maxCount due records.
        throw new NotImplementedException();
    }

    public Task<WebhookDeliveryLease> RenewLeaseAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        // Update only the current, unexpired token and never shorten ownership.
        throw new NotImplementedException();
    }

    public Task MarkSucceededAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default) =>
        TransitionTerminalAsync(lease, DeliveryState.Succeeded, completedAt, null, cancellationToken);

    public Task ScheduleRetryAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        DateTimeOffset nextAttemptAt,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        // Persist Failed + nextAttemptAt and clear active ownership using the current token.
        throw new NotImplementedException();
    }

    public Task MarkPermanentlyFailedAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? lastError,
        CancellationToken cancellationToken = default) =>
        TransitionTerminalAsync(
            lease,
            DeliveryState.PermanentlyFailed,
            completedAt,
            lastError,
            cancellationToken);

    public Task DeadLetterAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? lastError,
        CancellationToken cancellationToken = default) =>
        TransitionTerminalAsync(
            lease,
            DeliveryState.DeadLettered,
            completedAt,
            lastError,
            cancellationToken);

    public Task<WebhookDeliverySnapshot?> GetAsync(
        string webhookId,
        CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    private Task TransitionTerminalAsync(
        WebhookDeliveryLease lease,
        DeliveryState state,
        DateTimeOffset completedAt,
        string? lastError,
        CancellationToken cancellationToken)
    {
        // The persistence update must match the current, unexpired lease token or throw
        // WebhookDeliveryStoreConcurrencyException. Terminal records must never be claimable again.
        throw new NotImplementedException();
    }
}
```

The `NotImplementedException` calls are placeholders only. The backing implementation must enforce the atomicity and ownership rules in the persistence layer itself, then be exercised against the conformance behavior described in [`persistence.md`](persistence.md).

## Retry and dead-letter behavior

One call to `IWebhookDeliveryTransport.SendAsync` represents one HTTP attempt. The transport does not contain a retry loop.

The default classifier treats:

- `2xx` as success;
- `408`, `425`, `429`, and `5xx` as retryable;
- other HTTP statuses, including redirects, as permanent failures.

The default retry policy calculates a future `NextAttemptAt` using capped exponential backoff plus positive jitter. `MaxDelay` caps only the locally generated backoff/jitter delay. A valid positive `Retry-After` delta or future HTTP-date can delay the next attempt beyond `MaxDelay` and is not shortened by the default policy. When `MaxAttempts` is reached, a retryable failure becomes `DeadLettered`.

Permanent failures transition directly to `PermanentlyFailed` and are not retried automatically.

## Leases, concurrency, and shutdown

`MaxConcurrency` bounds the number of deliveries processed simultaneously. The dispatcher only claims enough records to fill currently available capacity.

A claim creates a lease. The lease token is proof of active ownership. State transitions must succeed only for the current, unexpired token. If a worker loses ownership because the lease expires and another worker reclaims the record, the stale worker must not overwrite the new owner's state.

While a delivery attempt is running, `WebhookDispatcher` renews the active lease every half `LeaseDuration`, capped at a 30-second renewal interval. Renewal keeps the same lease token and does not increment the attempt count. If renewal loses ownership or fails, the dispatcher cancels the in-flight attempt when possible and leaves the delivery for the current owner or future recovery instead of persisting a stale outcome.

For distributed durable stores, lease eligibility and renewal must be evaluated with one authoritative clock at the store/coordination boundary. Prefer backend/store time over individual worker clocks. If an adapter accepts worker-provided time, document and enforce a bounded-skew rule so a fast worker cannot prematurely reclaim another worker's active lease.

Shutdown is two-phase:

1. stop claiming new work;
2. wait up to `ShutdownGracePeriod` for in-flight work, then cancel remaining attempts.

Canceled or abandoned work is not marked successful. A durable store allows its lease to expire and the delivery to be reclaimed later.

## Signing outbound webhooks

Register a secret provider and the built-in HMAC signer:

```csharp
services.AddSingleton<IWebhookSigningSecretProvider, MySigningSecretProvider>();
services.AddSingleton<IWebhookRequestSigner, HmacSha256WebhookRequestSigner>();
```

Secrets should come from a secret manager or another protected application source. Do not put shared secrets in logs, metrics, traces, source control, or exception messages.

With default signing options, requests contain:

- `X-Webhook-Id`;
- `X-Webhook-Event`;
- `X-Webhook-Timestamp`;
- `X-Webhook-Signature` in the form `v1=<lowercase hex digest>`.

The signed bytes are exactly:

```text
UTF8(unixTimestampSeconds + ".") || rawRequestBodyBytes
```

`X-Webhook-Id` and `X-Webhook-Event` are delivery metadata but are **not included in those canonical HMAC bytes**. If a receiver relies on either value for authorization, idempotency, or routing, put the authoritative value in the signed payload as well and compare the header with that signed value after signature verification. The runnable sample demonstrates this cross-check for the webhook ID.

### Receiver-side verification

Verify the raw request body before parsing or reserializing it. A receiver should also reject timestamps outside a small replay-tolerance window and compare HMAC digests in constant time.

```csharp
static bool Verify(
    string timestampText,
    string signatureText,
    ReadOnlySpan<byte> body,
    ReadOnlySpan<byte> sharedSecret)
{
    if (!signatureText.StartsWith("v1=", StringComparison.Ordinal))
    {
        return false;
    }

    byte[] suppliedDigest;
    try
    {
        suppliedDigest = Convert.FromHexString(signatureText[3..]);
    }
    catch (FormatException)
    {
        return false;
    }

    byte[] key = sharedSecret.ToArray();
    byte[] prefix = Encoding.UTF8.GetBytes(timestampText + ".");

    try
    {
        using IncrementalHash hmac =
            IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(prefix);
        hmac.AppendData(body);
        byte[] expectedDigest = hmac.GetHashAndReset();

        return expectedDigest.Length == suppliedDigest.Length
            && CryptographicOperations.FixedTimeEquals(expectedDigest, suppliedDigest);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(key);
    }
}
```

The runnable sample includes timestamp parsing and replay-window validation around this same calculation.

## Receiver idempotency

Signature verification authenticates the timestamp and raw body bytes; it does not make receiver processing idempotent and does not authenticate metadata headers that are outside the canonical bytes.

A typical receiver should:

1. validate the signature and replay window against the raw body;
2. read a stable webhook ID from the authenticated payload, or cross-check a metadata header against the signed payload before trusting it;
3. atomically check that stable ID in its own idempotency store;
4. if already completed, return the same successful response without repeating side effects;
5. otherwise perform the business operation and persist the completed webhook ID in the same transaction whenever possible;
6. return success only after the receiver has durably committed its own work.

Do not rely on attempt number, timestamp, TCP connection identity, or request arrival time as an idempotency key.

## Observability

ReliableWebhooks uses standard .NET diagnostics and does not require a telemetry vendor.

Subscribe to:

```csharp
ReliableWebhooksInstrumentation.ActivitySourceName // ReliableWebhooks
ReliableWebhooksInstrumentation.MeterName          // ReliableWebhooks
```

The library emits structured logs for enqueue, claim, attempt, success, retry, permanent failure, dead letter, cancellation, lease loss, and unexpected failures. `InstrumentedWebhookDeliveryStore` adds enqueue logging and the queued metric around any store implementation.

Do not add payloads, signatures, secrets, full destination URLs, customer IDs, or other high-cardinality/sensitive values to metrics. The built-in metric dimensions intentionally remain bounded.

OpenTelemetry consumers can register the activity source and meter in their own application pipeline without adding an OpenTelemetry dependency to ReliableWebhooks itself.

## Configuration reference

| Setting | Default | Valid values / guidance |
| --- | --- | --- |
| `Dispatcher.MaxConcurrency` | `4` | Integer greater than zero |
| `Dispatcher.LeaseDuration` | `1 minute` | Positive duration; choose longer than a normal attempt or renew ownership |
| `Dispatcher.PollInterval` | `1 second` | Positive duration |
| `Dispatcher.ShutdownGracePeriod` | `30 seconds` | Zero or greater |
| `Retry.MaxAttempts` | `5` | Integer greater than zero; includes the current attempt |
| `Retry.BaseDelay` | `1 second` | Positive duration |
| `Retry.MaxDelay` | `5 minutes` | Positive and greater than or equal to `BaseDelay` |
| `Retry.JitterFactor` | `0.2` | Finite number from `0` through `1` |
| `Transport.AttemptTimeout` | `30 seconds` | Positive duration or `Timeout.InfiniteTimeSpan` |
| `Transport.MaxResponseBodyBytes` | `16 KiB` | Zero or greater |
| Signing header names | `X-Webhook-*` defaults | Non-empty and unique, case-insensitively |
| Signing time provider | `TimeProvider.System` | Non-null |

The DI-managed `HttpClient` has automatic redirects disabled, default `HttpClientFactory` request logging removed, and an infinite `HttpClient.Timeout`; `Transport.AttemptTimeout` is the authoritative per-attempt timeout. Applications can add handlers or other client configuration through `ReliableWebhooksBuilder.HttpClientBuilder`.

Default HTTP client logging is removed because webhook destination paths and query strings often carry endpoint secrets. Applications that deliberately add raw HTTP request logging back to `ReliableWebhooksBuilder.HttpClientBuilder` must treat destination URIs and custom header values as sensitive.

Invalid options fail during host startup with actionable validation messages.

## Safe production defaults and tuning

Start with the defaults, then tune from observed latency and backlog rather than maximizing concurrency blindly.

- Set `LeaseDuration` high enough for the backing store and network to renew comfortably; active attempts renew at half the lease duration, capped at 30 seconds.
- Keep `AttemptTimeout` bounded unless another cancellation mechanism is guaranteed.
- Increase `MaxConcurrency` only when the backing store, outbound network, and receivers can sustain it.
- Keep event types to a bounded vocabulary because event type is used as a metric dimension.
- Configure retry delays to avoid synchronized retry storms across many workers.
- Treat dead-lettered deliveries as operational work that needs monitoring and an explicit replay/remediation process.

## Troubleshooting

### The host fails at startup

Read the options-validation exception. Common causes are zero/negative concurrency or durations, `MaxDelay < BaseDelay`, an invalid jitter factor, duplicate signing header names, or a null time provider.

### No webhook is delivered

Confirm that a store is registered. `AddReliableWebhooks` intentionally does not register a production store. Then check that the hosted dispatcher was enabled (or that `WebhookDispatcher.RunAsync` is being run manually), the delivery is due, and its lease is not currently owned by another worker.

### A webhook is delivered more than once

This can be expected under at-least-once semantics. Verify that the receiver deduplicates by a stable ID authenticated by the signed payload. Also inspect worker crashes, attempt timeouts, lease duration, and failures while persisting success.

### Work remains `InProgress`

A worker may have crashed or been canceled. A conforming store makes the record reclaimable after its lease expires. If it remains stuck after expiration, inspect the store adapter's atomic claim and expiration predicates.

### A delivery goes directly to `PermanentlyFailed`

The default classifier treats most `4xx` responses as permanent. Confirm the receiver status code and replace `IWebhookHttpResponseClassifier` only when your API contract intentionally uses different semantics.

### Retries happen later than expected

Check `Retry-After`, exponential backoff, jitter, `MaxDelay`, and the dispatcher polling interval. A server-provided `Retry-After` can move the next attempt later than the local backoff.

### Signature verification fails

Verify the raw body bytes, timestamp text, shared secret, and signing-header names. Do not parse and reserialize JSON before calculating the HMAC. Ensure the receiver includes `timestamp + "."` before the exact body bytes and compares the digest in constant time.

### Process restarts lose queued work

`InMemoryWebhookDeliveryStore` is not durable. Register a conforming durable `IWebhookDeliveryStore` backed by the persistence technology appropriate for the application.

## What ReliableWebhooks does not guarantee

ReliableWebhooks does not provide exactly-once delivery, distributed transactions between the sender and receiver, receiver-side idempotency, persistence durability independent of the selected store, automatic dead-letter replay, secret rotation policy, or a mandatory storage/ORM adapter.

Those concerns remain explicit application or adapter responsibilities rather than hidden guarantees of the core library.
