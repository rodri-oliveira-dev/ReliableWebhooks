namespace ReliableWebhooks;

/// <summary>
/// Represents the transport-neutral outcome of one webhook delivery attempt.
/// </summary>
public enum WebhookDeliveryOutcome
{
    /// <summary>
    /// The delivery completed successfully and should not be retried.
    /// </summary>
    Success = 0,

    /// <summary>
    /// The delivery failed in a way that may succeed on a later attempt.
    /// </summary>
    RetryableFailure = 1,

    /// <summary>
    /// The delivery failed in a way that should not be retried by default.
    /// </summary>
    PermanentFailure = 2,
}
