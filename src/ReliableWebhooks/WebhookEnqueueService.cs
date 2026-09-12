namespace ReliableWebhooks;

internal sealed class WebhookEnqueueService : IWebhookEnqueueService
{
    private readonly IWebhookDeliveryStore store;
    private readonly TimeProvider timeProvider;

    internal WebhookEnqueueService(
        IWebhookDeliveryStore store,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.store = store;
        this.timeProvider = timeProvider;
    }

    public Task<WebhookEnqueueResult> EnqueueAsync(
        WebhookMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        return store.EnqueueAsync(
            message,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }
}
