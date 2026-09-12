namespace ReliableWebhooks;

/// <summary>
/// Provides an application-facing API for enqueueing outbound webhooks through the configured delivery store.
/// </summary>
public interface IWebhookEnqueueService
{
    /// <summary>
    /// Enqueues a webhook for delivery as soon as it becomes claimable.
    /// </summary>
    /// <param name="message">The immutable webhook message to enqueue.</param>
    /// <param name="cancellationToken">A token used to cancel the persistence operation.</param>
    /// <returns>The deterministic enqueue result returned by the configured store.</returns>
    Task<WebhookEnqueueResult> EnqueueAsync(
        WebhookMessage message,
        CancellationToken cancellationToken = default);
}
