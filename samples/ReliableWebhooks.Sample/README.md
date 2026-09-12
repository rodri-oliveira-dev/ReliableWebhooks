# ReliableWebhooks end-to-end sample

This sample runs a complete local delivery flow using the standard .NET host integration.

It demonstrates:

- `AddReliableWebhooks(...)` and `AddHostedDispatcher()`;
- `IWebhookEnqueueService` for application-facing enqueue;
- HMAC-SHA256 signing and receiver-side constant-time verification;
- immediate success;
- a retryable `503` followed by success;
- repeated retryable failures ending in `DeadLettered`;
- structured library logs, `ActivitySource` traces, and `Meter` metrics;
- `InstrumentedWebhookDeliveryStore` wrapping a store selected by the application.

Run it from the repository root:

```bash
dotnet run --project samples/ReliableWebhooks.Sample
```

The sample listens only on `http://127.0.0.1:5080`, explicitly sets `AllowInsecureHttp = true` for that loopback development endpoint, sends three webhooks to its own local receiver endpoints, prints diagnostic events and final states, and then stops automatically.

Expected terminal states are:

```text
sample-success: Succeeded after 1 attempt(s)
sample-retry: Succeeded after 2 attempt(s)
sample-dead-letter: DeadLettered after 3 attempt(s)
```

> `InMemoryWebhookDeliveryStore` is used only so the sample is runnable without external infrastructure. It is process-local and is **not** a production persistence choice. A production application must register a durable `IWebhookDeliveryStore` that satisfies the persistence contract documented in [`../../docs/persistence.md`](../../docs/persistence.md).

For production configuration, delivery guarantees, receiver idempotency, HMAC verification, observability, and troubleshooting, see [`../../docs/production-usage.md`](../../docs/production-usage.md).
