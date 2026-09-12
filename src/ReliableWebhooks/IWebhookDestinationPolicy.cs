namespace ReliableWebhooks;

/// <summary>
/// Authorizes a webhook destination before an outbound delivery request is sent.
/// </summary>
public interface IWebhookDestinationPolicy
{
    /// <summary>
    /// Authorizes <paramref name="destination"/> for one outbound delivery attempt.
    /// </summary>
    /// <param name="destination">The absolute HTTP or HTTPS destination URI.</param>
    /// <param name="cancellationToken">A token used to cancel the authorization operation.</param>
    /// <returns>A decision that indicates whether the transport may send to the destination.</returns>
    ValueTask<WebhookDestinationPolicyResult> AuthorizeAsync(
        Uri destination,
        CancellationToken cancellationToken = default);
}
