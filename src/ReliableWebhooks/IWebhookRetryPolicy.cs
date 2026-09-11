namespace ReliableWebhooks;

/// <summary>
/// Calculates retry scheduling decisions for retryable webhook delivery failures.
/// </summary>
public interface IWebhookRetryPolicy
{
    /// <summary>
    /// Calculates the next scheduling decision for a retryable delivery failure.
    /// </summary>
    /// <param name="context">The current delivery and retry metadata.</param>
    /// <returns>A scheduling decision that either retries later or dead-letters the delivery.</returns>
    WebhookRetryDecision GetDecision(WebhookRetryContext context);
}
