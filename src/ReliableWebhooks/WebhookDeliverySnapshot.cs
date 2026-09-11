namespace ReliableWebhooks;

/// <summary>
/// Represents the immutable persisted state of one webhook delivery.
/// </summary>
public sealed class WebhookDeliverySnapshot
{
    internal WebhookDeliverySnapshot(
        WebhookMessage message,
        DeliveryState state,
        int attemptCount,
        DateTimeOffset? nextAttemptAt,
        string? lastError,
        DateTimeOffset? leaseExpiresAt)
    {
        Message = message;
        State = state;
        AttemptCount = attemptCount;
        NextAttemptAt = nextAttemptAt;
        LastError = lastError;
        LeaseExpiresAt = leaseExpiresAt;
    }

    /// <summary>
    /// Gets the webhook message associated with the delivery.
    /// </summary>
    public WebhookMessage Message
    {
        get;
    }

    /// <summary>
    /// Gets the current delivery state.
    /// </summary>
    public DeliveryState State
    {
        get;
    }

    /// <summary>
    /// Gets the number of claims that have started processing this delivery.
    /// </summary>
    public int AttemptCount
    {
        get;
    }

    /// <summary>
    /// Gets the earliest time at which the next attempt may be claimed, when applicable.
    /// </summary>
    public DateTimeOffset? NextAttemptAt
    {
        get;
    }

    /// <summary>
    /// Gets the last recorded transport-neutral error, when available.
    /// </summary>
    public string? LastError
    {
        get;
    }

    /// <summary>
    /// Gets the current lease expiration time when the delivery is in progress.
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt
    {
        get;
    }

    /// <summary>
    /// Gets a value indicating whether no further automatic processing should occur.
    /// </summary>
    public bool IsTerminal => State is DeliveryState.Succeeded
        or DeliveryState.PermanentlyFailed
        or DeliveryState.DeadLettered;
}
