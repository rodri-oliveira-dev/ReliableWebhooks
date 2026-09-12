namespace ReliableWebhooks;

/// <summary>
/// Sends one webhook delivery attempt and returns a transport-neutral result.
/// </summary>
public interface IWebhookDeliveryTransport
{
    /// <summary>
    /// Sends one attempt for <paramref name="message"/>.
    /// </summary>
    /// <param name="message">The webhook message to send.</param>
    /// <param name="cancellationToken">A token used to cancel the attempt.</param>
    /// <returns>The classified result of the delivery attempt.</returns>
    Task<WebhookDeliveryResult> SendAsync(
        WebhookMessage message,
        CancellationToken cancellationToken = default);
}
