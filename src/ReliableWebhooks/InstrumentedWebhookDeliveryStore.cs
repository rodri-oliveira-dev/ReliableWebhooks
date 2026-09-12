using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReliableWebhooks;

/// <summary>
/// Decorates an <see cref="IWebhookDeliveryStore"/> and emits enqueue telemetry without coupling
/// instrumentation to a specific persistence implementation.
/// </summary>
public sealed class InstrumentedWebhookDeliveryStore : IWebhookDeliveryStore
{
    private readonly IWebhookDeliveryStore innerStore;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instrumented store using a no-op logger.
    /// </summary>
    /// <param name="innerStore">The store that performs persistence operations.</param>
    public InstrumentedWebhookDeliveryStore(IWebhookDeliveryStore innerStore)
        : this(innerStore, NullLogger.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instrumented store.
    /// </summary>
    /// <param name="innerStore">The store that performs persistence operations.</param>
    /// <param name="logger">The logger that receives safe structured enqueue events.</param>
    public InstrumentedWebhookDeliveryStore(IWebhookDeliveryStore innerStore, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(innerStore);
        ArgumentNullException.ThrowIfNull(logger);

        this.innerStore = innerStore;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<WebhookEnqueueResult> EnqueueAsync(
        WebhookMessage message,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        WebhookEnqueueResult result = await innerStore
            .EnqueueAsync(message, nextAttemptAt, cancellationToken)
            .ConfigureAwait(false);

        if (result.WasEnqueued)
        {
            ReliableWebhooksLog.Enqueued(logger, message.Id, message.EventType);
            ReliableWebhooksInstrumentation.Queued.Add(
                1,
                new KeyValuePair<string, object?>(
                    ReliableWebhooksInstrumentation.EventTypeTagName,
                    message.EventType));
        }

        return result;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WebhookDeliveryLease>> ClaimDueAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        return innerStore.ClaimDueAsync(now, leaseDuration, maxCount, cancellationToken);
    }

    /// <inheritdoc />
    public Task<WebhookDeliveryLease> RenewLeaseAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        return innerStore.RenewLeaseAsync(lease, now, leaseDuration, cancellationToken);
    }

    /// <inheritdoc />
    public Task MarkSucceededAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        return innerStore.MarkSucceededAsync(lease, completedAt, cancellationToken);
    }

    /// <inheritdoc />
    public Task ScheduleRetryAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        DateTimeOffset nextAttemptAt,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        return innerStore.ScheduleRetryAsync(
            lease,
            completedAt,
            nextAttemptAt,
            lastError,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task MarkPermanentlyFailedAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        return innerStore.MarkPermanentlyFailedAsync(
            lease,
            completedAt,
            lastError,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task DeadLetterAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        return innerStore.DeadLetterAsync(
            lease,
            completedAt,
            lastError,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<WebhookDeliverySnapshot?> GetAsync(
        string webhookId,
        CancellationToken cancellationToken = default)
    {
        return innerStore.GetAsync(webhookId, cancellationToken);
    }
}
