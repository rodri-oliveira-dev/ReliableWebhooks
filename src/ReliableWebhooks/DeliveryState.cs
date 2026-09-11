namespace ReliableWebhooks;

/// <summary>
/// Describes the current lifecycle state of a webhook delivery.
/// </summary>
public enum DeliveryState
{
    /// <summary>
    /// The webhook is waiting to be processed.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// The webhook is currently being processed by a dispatcher.
    /// </summary>
    InProgress = 1,

    /// <summary>
    /// The webhook was delivered successfully.
    /// </summary>
    Succeeded = 2,

    /// <summary>
    /// The latest delivery attempt failed and the delivery is not currently being processed.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// The webhook reached a terminal state and will not be retried automatically.
    /// </summary>
    DeadLettered = 4,
}
