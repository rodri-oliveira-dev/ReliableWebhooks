# Persistence contract

ReliableWebhooks deliberately does not choose a database, ORM, cache, file format, or other storage technology. The core package owns the delivery protocol through `IWebhookDeliveryStore`; the application owns the persistence adapter.

A production application can implement the contract with Entity Framework Core, Dapper, raw ADO.NET, a relational or document database, Redis, files, or another mechanism, provided that implementation satisfies the reliability semantics below.

## What a store must preserve

A durable implementation must preserve enough information to resume delivery after the durability boundary it advertises has been crossed.

| Data | Requirement |
| --- | --- |
| Stable webhook ID | Preserve exactly and treat as an opaque, case-sensitive idempotency key. |
| Event type | Preserve exactly. |
| Destination | Preserve the complete absolute HTTP/HTTPS URI required for delivery. |
| Payload | Preserve the exact payload bytes. Do not reserialize or normalize them. |
| Content type | Preserve exactly. |
| Custom headers | Preserve names and values required for subsequent attempts. |
| Delivery state | Preserve the current `DeliveryState`. |
| Attempt count | Increment exactly once for each successful claim. |
| Next attempt time | Preserve retry scheduling for pending/failed work. |
| Last error | Preserve the latest transport-neutral error when supplied. |
| Lease token | Persist the current opaque ownership token even though it is not exposed on `WebhookDeliverySnapshot`. |
| Lease expiration | Persist the time at which current ownership expires. |

`InMemoryWebhookDeliveryStore` implements the behavioral protocol but is process-local and loses all state when the process exits. It is suitable for tests, samples, and local scenarios only.

## Idempotent enqueue

`EnqueueAsync` uses `WebhookMessage.Id` as the stable idempotency key.

The first successful enqueue persists the message and initial scheduling state. Any later enqueue using the same ID must return `AlreadyExists` with the current stored delivery and must not overwrite payload, destination, headers, scheduling state, attempts, or terminal state even when the new message contains different data.

Production stores should enforce this invariant at the persistence boundary, for example through a unique key, conditional insert, compare-and-set operation, or equivalent primitive supplied by the chosen technology.

## Atomic claims and leases

`ClaimDueAsync` may claim pending or failed deliveries whose `NextAttemptAt` is due, and in-progress deliveries whose previous lease has expired.

For every successful claim the implementation must atomically:

1. establish that the delivery is eligible;
2. transition it to `InProgress`;
3. increment `AttemptCount` exactly once;
4. assign a new non-empty lease token;
5. establish the lease expiration;
6. return a `WebhookDeliveryLease` that represents that ownership.

Competing workers must never both receive simultaneously valid leases for the same delivery. An in-process `lock` can satisfy this requirement only for a process-local implementation such as `InMemoryWebhookDeliveryStore`; it is not sufficient for a production store shared by multiple processes or hosts.

The concrete atomicity mechanism is intentionally left to the adapter. Transactions, conditional updates, optimistic concurrency, row/version tokens, compare-and-set operations, or storage-specific claim primitives are all valid approaches.

## Authoritative clock

Claim, renewal, and lease-expiration decisions must use one authoritative clock for the store's coordination boundary. `InMemoryWebhookDeliveryStore` and deterministic tests use the caller-supplied `now` value as that authority because all workers are in one process.

Distributed durable stores should prefer backend/store time, such as a database server timestamp or another shared coordination clock, when deciding whether work is due or a lease has expired. If an adapter instead relies on worker-provided time, it must define and enforce a maximum skew/tolerance rule that is strong enough to prevent two workers with different clocks from owning the same delivery at the same time.

Retry scheduling and terminal timestamps may use the dispatcher-supplied operation time, but a store must never let a worker's fast local clock prematurely expire another worker's active lease outside the documented authoritative-time model.

## Lease renewal and stale ownership

`WebhookDispatcher` renews an in-flight delivery lease while the transport attempt is still running. The renewal cadence is one half of `Dispatcher.LeaseDuration`, capped at 30 seconds, so renewal normally happens with a safety margin instead of waiting for the expiration boundary.

`RenewLeaseAsync` must preserve the same lease token, must not increment `AttemptCount`, and must not shorten the currently valid lease. The returned `WebhookDeliveryLease` should reflect the current persisted expiration so callers can use the latest ownership snapshot for the final state transition.

If renewal fails because ownership is stale, replaced, expired, or otherwise invalid, the dispatcher cancels the in-flight attempt when possible and does not persist success, retry, permanent-failure, or dead-letter state for that stale owner.

Every state-changing operation that accepts a `WebhookDeliveryLease` must verify that:

- the delivery still exists;
- it is still `InProgress`;
- the supplied token is still the current owner;
- the lease has not expired at the operation time.

If ownership is stale, replaced, expired, or otherwise invalid, the operation must throw `WebhookDeliveryStoreConcurrencyException` without mutating the current state.

This check must happen at the persistence boundary. Reading ownership and updating later without concurrency protection introduces a race and is not conforming behavior.

## State transitions

A conforming implementation must apply these observable transitions:

| Operation | Result |
| --- | --- |
| `ScheduleRetryAsync` | `Failed`, retains the attempt count, records `LastError`, stores `NextAttemptAt`, clears lease ownership. |
| `MarkSucceededAsync` | `Succeeded`, clears scheduling and lease ownership, and becomes terminal. |
| `MarkPermanentlyFailedAsync` | `PermanentlyFailed`, records `LastError`, clears scheduling and lease ownership, and becomes terminal. |
| `DeadLetterAsync` | `DeadLettered`, records `LastError`, clears scheduling and lease ownership, and becomes terminal. |

Terminal deliveries must never become claimable again through normal due-work claiming.

## Cancellation and persistence failures

All asynchronous store operations accept a `CancellationToken`. A token that is already canceled should be observed before committing an externally visible state change.

Backend failures must propagate to the caller. A store must never report a successful transition if the corresponding durable write failed or is known not to have committed. Provider-specific adapters should include fault-injection tests for transaction failures, connectivity failures, conflicts, or equivalent backend errors because the generic store abstraction cannot synthesize those failures itself.

## Conformance tests

The repository contains a reusable behavioral suite at:

`tests/ReliableWebhooks.Tests/Conformance/WebhookDeliveryStoreConformanceTests.cs`

Every in-repository persistence adapter should derive its test fixture from that suite and return a fresh isolated store:

```csharp
public sealed class MyStoreConformanceTests : WebhookDeliveryStoreConformanceTests
{
    protected override IWebhookDeliveryStore CreateStore()
    {
        return CreateIsolatedStoreForTest();
    }
}
```

The suite validates idempotent enqueue, concurrent claims, due-work filtering, lease reclaim, stale-owner rejection, renewal, retries, terminal transitions, and pre-canceled operations. A concrete adapter must add technology-specific tests for true durability, persistence-level uniqueness, multiprocess/process-host concurrency where relevant, migrations/schema concerns, and injected backend failures.

## Dependency injection

ReliableWebhooks never registers a production store implicitly. Applications choose the implementation explicitly:

```csharp
services.AddSingleton<IWebhookDeliveryStore, MyDurableWebhookStore>();

ReliableWebhooksBuilder webhooks = services.AddReliableWebhooks();
webhooks.AddHostedDispatcher();
```

The selected lifetime must match the adapter's own thread-safety and resource-management requirements. The ReliableWebhooks DI integration supports singleton, scoped, and transient `IWebhookDeliveryStore` registrations. DI-created singleton services such as `IWebhookEnqueueService`, `WebhookDispatcher`, and the hosted dispatcher do not capture the store from the root provider; they resolve it inside a short-lived operation scope for each store call.

Scoped and transient adapters must still coordinate through durable/shared backing state. A scoped store instance may wrap a scoped database session, unit of work, or client, but independent operation scopes must observe the same persisted deliveries, leases, and concurrency tokens.

## Delivery guarantees

The core package defines the protocol; the backing store supplies actual durability.

With a conforming durable store, ReliableWebhooks is designed for **at-least-once** outbound delivery. It does not provide exactly-once HTTP delivery. Receivers must remain idempotent and tolerate duplicate deliveries, including duplicates caused by a worker losing ownership after the remote endpoint has already accepted a request.
