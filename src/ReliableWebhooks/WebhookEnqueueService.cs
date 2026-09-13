namespace ReliableWebhooks;

internal sealed class WebhookEnqueueService : IWebhookEnqueueService
{
    private readonly IWebhookDeliveryStore store;
    private readonly TimeProvider timeProvider;
    private readonly WebhookMessageLimits limits;

    internal WebhookEnqueueService(
        IWebhookDeliveryStore store,
        TimeProvider timeProvider,
        WebhookMessageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(limits);

        this.store = store;
        this.timeProvider = timeProvider;
        this.limits = limits;
    }

    public Task<WebhookEnqueueResult> EnqueueAsync(
        WebhookMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        limits.Validate(message);

        return store.EnqueueAsync(
            message,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }
}
