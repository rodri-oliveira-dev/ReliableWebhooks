namespace ReliableWebhooks;

/// <summary>
/// Defines persistence operations required to coordinate reliable webhook delivery across workers.
/// </summary>
public interface IWebhookDeliveryStore
{
    /// <summary>
    /// Enqueues a webhook if its stable identifier has not been seen before.
    /// </summary>
    /// <param name="message">The webhook message to persist.</param>
    /// <param name="nextAttemptAt">The earliest time at which the webhook may be claimed.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The deterministic enqueue result and current persisted snapshot.</returns>
    Task<WebhookEnqueueResult> EnqueueAsync(
        WebhookMessage message,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims up to <paramref name="maxCount"/> due deliveries for processing.
    /// </summary>
    /// <param name="now">The time used to evaluate due work and expired leases.</param>
    /// <param name="leaseDuration">The duration granted to each claimed delivery.</param>
    /// <param name="maxCount">The maximum number of deliveries to claim.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>Leases owned by the caller. An expired active lease may be reclaimed with a new token.</returns>
    Task<IReadOnlyList<WebhookDeliveryLease>> ClaimDueAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews an active lease owned by the caller.
    /// </summary>
    /// <param name="lease">The lease to renew.</param>
    /// <param name="now">The time used to validate lease freshness.</param>
    /// <param name="leaseDuration">The lease duration measured from <paramref name="now"/>.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>The renewed lease snapshot.</returns>
    Task<WebhookDeliveryLease> RenewLeaseAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an actively leased delivery as successfully completed.
    /// </summary>
    Task MarkSucceededAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a retryable failure and schedules the next attempt.
    /// </summary>
    Task ScheduleRetryAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        DateTimeOffset nextAttemptAt,
        string? lastError,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an actively leased delivery as permanently failed without scheduling another attempt.
    /// </summary>
    Task MarkPermanentlyFailedAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? lastError,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an actively leased delivery to the dead-letter state.
    /// </summary>
    Task DeadLetterAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? lastError,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current persisted snapshot for a webhook identifier when it exists.
    /// </summary>
    Task<WebhookDeliverySnapshot?> GetAsync(
        string webhookId,
        CancellationToken cancellationToken = default);
}
