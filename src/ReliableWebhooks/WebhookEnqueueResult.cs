namespace ReliableWebhooks;

/// <summary>
/// Describes the result of an idempotent webhook enqueue operation.
/// </summary>
public enum WebhookEnqueueStatus
{
    /// <summary>
    /// A new delivery was persisted.
    /// </summary>
    Enqueued = 0,

    /// <summary>
    /// A delivery with the same stable webhook identifier already existed and was left unchanged.
    /// </summary>
    AlreadyExists = 1,
}

/// <summary>
/// Contains the deterministic result of an enqueue operation.
/// </summary>
public sealed class WebhookEnqueueResult
{
    /// <summary>
    /// Initializes a new enqueue result.
    /// </summary>
    /// <param name="status">The deterministic enqueue status.</param>
    /// <param name="delivery">The current persisted delivery snapshot.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is undefined.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="delivery"/> is <see langword="null"/>.</exception>
    public WebhookEnqueueResult(WebhookEnqueueStatus status, WebhookDeliverySnapshot delivery)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Enqueue status must be a defined value.");
        }

        ArgumentNullException.ThrowIfNull(delivery);

        Status = status;
        Delivery = delivery;
    }

    /// <summary>
    /// Gets the enqueue status.
    /// </summary>
    public WebhookEnqueueStatus Status
    {
        get;
    }

    /// <summary>
    /// Gets the current persisted delivery snapshot.
    /// </summary>
    public WebhookDeliverySnapshot Delivery
    {
        get;
    }

    /// <summary>
    /// Gets a value indicating whether this operation created the delivery.
    /// </summary>
    public bool WasEnqueued => Status == WebhookEnqueueStatus.Enqueued;
}
