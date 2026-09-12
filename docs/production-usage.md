# Production usage guide

This guide describes how to integrate ReliableWebhooks into a production .NET application without coupling the library to a specific persistence technology.

For a runnable local example, see [`samples/ReliableWebhooks.Sample`](../samples/ReliableWebhooks.Sample). For the full persistence-port semantics and adapter guidance, see [`persistence.md`](persistence.md).

## Delivery model

ReliableWebhooks is designed for **at-least-once delivery**, not exactly-once delivery. A delivery can reach the receiver more than once when a worker sends the HTTP request successfully but fails before persisting the successful transition, or when a lease expires while ownership is uncertain.

Receivers must therefore be idempotent. Use the stable `X-Webhook-Id` value, or an application-defined idempotency key carried in the payload, to record completed processing and safely ignore duplicate deliveries.

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

## Retry and dead-letter behavior

One call to `IWebhookDeliveryTransport.SendAsync` represents one HTTP attempt. The transport does not contain a retry loop.

The default classifier treats:

- `2xx` as success;
- `408`, `425`, `429`, and `5xx` as retryable;
- other HTTP statuses, including redirects, as permanent failures.

The default retry policy calculates a future `NextAttemptAt` using capped exponential backoff plus positive jitter. A valid `Retry-After` can delay the next attempt further, subject to `MaxDelay`. When `MaxAttempts` is reached, a retryable failure becomes `DeadLettered`.

Permanent failures transition directly to `PermanentlyFailed` and are not retried automatically.

## Leases, concurrency, and shutdown

`MaxConcurrency` bounds the number of deliveries processed simultaneously. The dispatcher only claims enough records to fill currently available capacity.

A claim creates a lease. The lease token is proof of active ownership. State transitions must succeed only for the current, unexpired token. If a worker loses ownership because the lease expires and another worker reclaims the record, the stale worker must not overwrite the new owner's state.

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

Signature verification authenticates the request; it does not make receiver processing idempotent.

A typical receiver should:

1. validate the signature and replay window;
2. atomically check the stable webhook ID in its own idempotency store;
3. if already completed, return the same successful response without repeating side effects;
4. otherwise perform the business operation and persist the completed webhook ID in the same transaction whenever possible;
5. return success only after the receiver has durably committed its own work.

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

The DI-managed `HttpClient` has automatic redirects disabled and an infinite `HttpClient.Timeout`; `Transport.AttemptTimeout` is the authoritative per-attempt timeout. Applications can add handlers or other client configuration through `ReliableWebhooksBuilder.HttpClientBuilder`.

Invalid options fail during host startup with actionable validation messages.

## Safe production defaults and tuning

Start with the defaults, then tune from observed latency and backlog rather than maximizing concurrency blindly.

- Set `LeaseDuration` comfortably above the expected request attempt duration, including network variance.
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

This can be expected under at-least-once semantics. Verify that the receiver deduplicates by stable webhook ID. Also inspect worker crashes, attempt timeouts, lease duration, and failures while persisting success.

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
