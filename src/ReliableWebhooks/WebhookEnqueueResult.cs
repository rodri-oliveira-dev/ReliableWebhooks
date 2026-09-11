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
    internal WebhookEnqueueResult(WebhookEnqueueStatus status, WebhookDeliverySnapshot delivery)
    {
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
