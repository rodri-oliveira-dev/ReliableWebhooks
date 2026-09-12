namespace ReliableWebhooks;

/// <summary>
/// Resolves the secret material used to sign an outbound webhook.
/// </summary>
/// <remarks>
/// Implementations can resolve secrets from application configuration, a secret manager, or another secure source.
/// The library does not log or otherwise expose the returned secret.
/// </remarks>
public interface IWebhookSigningSecretProvider
{
    /// <summary>
    /// Gets the secret material to use for the supplied webhook message.
    /// </summary>
    /// <param name="message">The webhook message being signed.</param>
    /// <param name="cancellationToken">A token used to cancel secret resolution.</param>
    /// <returns>The secret bytes used by the configured signer.</returns>
    ValueTask<ReadOnlyMemory<byte>> GetSecretAsync(
        WebhookMessage message,
        CancellationToken cancellationToken = default);
}
