namespace ReliableWebhooks;

/// <summary>
/// Produces a signature for one outbound webhook request.
/// </summary>
public interface IWebhookRequestSigner
{
    /// <summary>
    /// Signs one webhook delivery attempt using the supplied message metadata, payload, and timestamp.
    /// </summary>
    /// <param name="message">The webhook message being delivered.</param>
    /// <param name="payload">The exact payload bytes that will be sent in the HTTP request.</param>
    /// <param name="timestamp">The UTC timestamp associated with the signature.</param>
    /// <param name="cancellationToken">A token used to cancel secret resolution or signing work.</param>
    /// <returns>The value to place in the configured webhook signature header.</returns>
    ValueTask<string> SignAsync(
        WebhookMessage message,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default);
}
