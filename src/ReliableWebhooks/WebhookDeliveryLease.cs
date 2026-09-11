namespace ReliableWebhooks;

/// <summary>
/// Represents temporary ownership of one webhook delivery by a worker.
/// </summary>
public sealed class WebhookDeliveryLease
{
    internal WebhookDeliveryLease(
        WebhookDeliverySnapshot delivery,
        Guid token,
        DateTimeOffset expiresAt)
    {
        Delivery = delivery;
        Token = token;
        ExpiresAt = expiresAt;
    }

    /// <summary>
    /// Gets the persisted delivery snapshot captured when the lease was issued or renewed.
    /// </summary>
    public WebhookDeliverySnapshot Delivery
    {
        get;
    }

    /// <summary>
    /// Gets the opaque token that proves ownership of the current lease.
    /// </summary>
    public Guid Token
    {
        get;
    }

    /// <summary>
    /// Gets the time at which the lease expires if it is not renewed.
    /// </summary>
    public DateTimeOffset ExpiresAt
    {
        get;
    }
}
