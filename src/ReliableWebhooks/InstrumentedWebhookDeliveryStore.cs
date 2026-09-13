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
    private readonly WebhookMetricsOptions metricsOptions;

    /// <summary>
    /// Initializes a new instrumented store using a no-op logger.
    /// </summary>
    /// <param name="innerStore">The store that performs persistence operations.</param>
    public InstrumentedWebhookDeliveryStore(IWebhookDeliveryStore innerStore)
        : this(innerStore, NullLogger.Instance, new WebhookMetricsOptions())
    {
    }

    /// <summary>
    /// Initializes a new instrumented store using a no-op logger.
    /// </summary>
    /// <param name="innerStore">The store that performs persistence operations.</param>
    /// <param name="metricsOptions">The metric-dimension policy.</param>
    public InstrumentedWebhookDeliveryStore(
        IWebhookDeliveryStore innerStore,
        WebhookMetricsOptions metricsOptions)
        : this(innerStore, NullLogger.Instance, metricsOptions)
    {
    }

    /// <summary>
    /// Initializes a new instrumented store.
    /// </summary>
    /// <param name="innerStore">The store that performs persistence operations.</param>
    /// <param name="logger">The logger that receives safe structured enqueue events.</param>
    public InstrumentedWebhookDeliveryStore(IWebhookDeliveryStore innerStore, ILogger logger)
        : this(innerStore, logger, new WebhookMetricsOptions())
    {
    }

    /// <summary>
    /// Initializes a new instrumented store.
    /// </summary>
    /// <param name="innerStore">The store that performs persistence operations.</param>
    /// <param name="logger">The logger that receives safe structured enqueue events.</param>
    /// <param name="metricsOptions">The metric-dimension policy.</param>
    public InstrumentedWebhookDeliveryStore(
        IWebhookDeliveryStore innerStore,
        ILogger logger,
        WebhookMetricsOptions metricsOptions)
    {
        ArgumentNullException.ThrowIfNull(innerStore);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(metricsOptions);

        this.innerStore = innerStore;
        this.logger = logger;
        this.metricsOptions = metricsOptions;
    }

    /// <inheritdoc />
    public async Task<WebhookEnqueueResult> EnqueueAsync(
        WebhookMessage message,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        WebhookEnqueueResult result = await innerStore
            .EnqueueAsync(message, nextAttemptAt, cancellationToken)
            .ConfigureAwait(false);

        if (result.WasEnqueued)
        {
            ReliableWebhooksLog.Enqueued(logger, message.Id, message.EventType);
            ReliableWebhooksInstrumentation.Queued.Add(
                1,
                ReliableWebhooksInstrumentation.CreateEventTypeMetricTags(
                    metricsOptions,
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
