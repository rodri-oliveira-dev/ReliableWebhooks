namespace ReliableWebhooks;

/// <summary>
/// Resolves outbound request headers immediately before one webhook delivery attempt is sent.
/// </summary>
/// <remarks>
/// Use this abstraction for sensitive per-request credentials, such as bearer tokens or cookies, that should
/// not be embedded in the persisted <see cref="WebhookMessage"/>. Implementations should resolve values from
/// a secret manager or another protected source and must not log or expose returned header values.
/// </remarks>
public interface IWebhookRequestHeaderProvider
{
    /// <summary>
    /// Gets additional request headers for the supplied webhook message.
    /// </summary>
    /// <param name="message">The webhook message being delivered.</param>
    /// <param name="cancellationToken">A token used to cancel header resolution.</param>
    /// <returns>Headers to add to the outbound HTTP request for this attempt.</returns>
    ValueTask<IReadOnlyDictionary<string, string>> GetHeadersAsync(
        WebhookMessage message,
        CancellationToken cancellationToken = default);
}
