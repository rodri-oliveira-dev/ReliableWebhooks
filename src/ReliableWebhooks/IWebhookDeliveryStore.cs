namespace ReliableWebhooks;

/// <summary>
/// Defines the technology-agnostic persistence contract required to coordinate reliable webhook delivery across workers.
/// </summary>
/// <remarks>
/// <para>
/// Implementations may use any persistence technology, but production stores must provide durability and atomicity
/// appropriate to their advertised deployment model. In-process locking alone is insufficient when multiple processes
/// or hosts can operate on the same deliveries.
/// </para>
/// <para>
/// A store must preserve the complete <see cref="WebhookMessage"/>, <see cref="DeliveryState"/>, attempt count,
/// retry scheduling metadata, last error, and active lease ownership/expiration required to resume delivery safely.
/// Stable webhook identifiers are opaque and case-sensitive. Duplicate enqueue operations for the same identifier are
/// idempotent and must not replace or mutate the originally persisted delivery.
/// </para>
/// <para>
/// Claiming must be atomic from the perspective of competing workers: at most one active lease may own a delivery at
/// a time. Expired leases may be reclaimed with a new token. Mutating operations performed with an expired, replaced,
/// or otherwise stale lease must fail with <see cref="WebhookDeliveryStoreConcurrencyException"/> and must not alter
/// the current owner or delivery state.
/// </para>
/// <para>
/// Persistence failures must be surfaced to the caller; implementations must never report a successful transition when
/// the corresponding durable state change did not complete. Operations should observe cancellation before committing
/// an externally visible state change when the supplied token is already canceled.
/// </para>
/// </remarks>
public interface IWebhookDeliveryStore
{
    /// <summary>
    /// Enqueues a webhook if its stable identifier has not been seen before.
    /// </summary>
    /// <param name="message">The webhook message to persist exactly, including payload bytes and custom headers.</param>
    /// <param name="nextAttemptAt">The earliest time at which the webhook may be claimed.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>
    /// <see cref="WebhookEnqueueStatus.Enqueued"/> with the newly persisted snapshot, or
    /// <see cref="WebhookEnqueueStatus.AlreadyExists"/> with the current persisted snapshot for the existing identifier.
    /// A duplicate enqueue must not overwrite the existing message or scheduling state.
    /// </returns>
    Task<WebhookEnqueueResult> EnqueueAsync(
        WebhookMessage message,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims up to <paramref name="maxCount"/> due deliveries for processing.
    /// </summary>
    /// <param name="now">The time used to evaluate due work and expired leases.</param>
    /// <param name="leaseDuration">The positive duration granted to each claimed delivery.</param>
    /// <param name="maxCount">The positive maximum number of deliveries to claim.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>
    /// At most <paramref name="maxCount"/> leases owned by the caller. Eligible work consists of pending or failed
    /// deliveries whose next-attempt time is less than or equal to <paramref name="now"/>, plus in-progress deliveries
    /// whose lease has expired at or before <paramref name="now"/>. No ordering between eligible deliveries is required.
    /// </returns>
    /// <remarks>
    /// Each successful claim transitions the delivery to <see cref="DeliveryState.InProgress"/>, increments the attempt
    /// count exactly once, assigns a new non-empty lease token, and establishes a lease expiration. Competing claims must
    /// not return simultaneously valid leases for the same delivery.
    /// </remarks>
    Task<IReadOnlyList<WebhookDeliveryLease>> ClaimDueAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews an active lease owned by the caller.
    /// </summary>
    /// <param name="lease">The current lease proving ownership.</param>
    /// <param name="now">The time used to validate lease freshness.</param>
    /// <param name="leaseDuration">The positive lease duration measured from <paramref name="now"/>.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The renewed lease snapshot using the same ownership token.</returns>
    /// <remarks>
    /// Renewal must not increment the attempt count or shorten an existing lease. An expired or stale lease must be
    /// rejected with <see cref="WebhookDeliveryStoreConcurrencyException"/>.
    /// </remarks>
    Task<WebhookDeliveryLease> RenewLeaseAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an actively leased delivery as successfully completed.
    /// </summary>
    /// <remarks>
    /// The transition is terminal, clears retry scheduling and lease ownership, and must reject stale ownership with
    /// <see cref="WebhookDeliveryStoreConcurrencyException"/>.
    /// </remarks>
    Task MarkSucceededAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a retryable failure and schedules the next attempt.
    /// </summary>
    /// <remarks>
    /// The transition sets <see cref="DeliveryState.Failed"/>, preserves the current attempt count, records the error,
    /// clears lease ownership, and makes the delivery claimable only when <paramref name="nextAttemptAt"/> is reached.
    /// The next-attempt time must not precede <paramref name="completedAt"/>.
    /// </remarks>
    Task ScheduleRetryAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        DateTimeOffset nextAttemptAt,
        string? lastError,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an actively leased delivery as permanently failed without scheduling another attempt.
    /// </summary>
    /// <remarks>
    /// The transition is terminal, records the error, clears retry scheduling and lease ownership, and must reject
    /// stale ownership with <see cref="WebhookDeliveryStoreConcurrencyException"/>.
    /// </remarks>
    Task MarkPermanentlyFailedAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? lastError,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an actively leased delivery to the dead-letter state.
    /// </summary>
    /// <remarks>
    /// The transition is terminal, records the error, clears retry scheduling and lease ownership, and must reject
    /// stale ownership with <see cref="WebhookDeliveryStoreConcurrencyException"/>.
    /// </remarks>
    Task DeadLetterAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? lastError,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current persisted snapshot for a webhook identifier when it exists.
    /// </summary>
    /// <remarks>
    /// This operation is observational and must not claim, renew, or otherwise mutate the delivery.
    /// </remarks>
    Task<WebhookDeliverySnapshot?> GetAsync(
        string webhookId,
        CancellationToken cancellationToken = default);
}
