namespace ReliableWebhooks;

/// <summary>
/// Represents temporary ownership of one webhook delivery by a worker.
/// </summary>
public sealed class WebhookDeliveryLease
{
    /// <summary>
    /// Initializes a new delivery lease.
    /// </summary>
    /// <param name="delivery">The persisted delivery snapshot captured for the lease.</param>
    /// <param name="token">The opaque token that proves ownership of the lease.</param>
    /// <param name="expiresAt">The time at which the lease expires if it is not renewed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="delivery"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="token"/> is empty.</exception>
    public WebhookDeliveryLease(
        WebhookDeliverySnapshot delivery,
        Guid token,
        DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        if (token == Guid.Empty)
        {
            throw new ArgumentException("Lease token cannot be empty.", nameof(token));
        }

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
