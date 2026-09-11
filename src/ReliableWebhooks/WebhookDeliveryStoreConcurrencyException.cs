namespace ReliableWebhooks;

/// <summary>
/// Represents a rejected store update caused by a stale, expired, or otherwise invalid delivery lease.
/// </summary>
public sealed class WebhookDeliveryStoreConcurrencyException : InvalidOperationException
{
    internal WebhookDeliveryStoreConcurrencyException(string webhookId, string message)
        : base(message)
    {
        WebhookId = webhookId;
    }

    /// <summary>
    /// Gets the webhook identifier associated with the rejected update.
    /// </summary>
    public string WebhookId
    {
        get;
    }
}
