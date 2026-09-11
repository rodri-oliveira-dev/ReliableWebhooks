namespace ReliableWebhooks;

/// <summary>
/// Provides the default HTTP response classification used by <see cref="WebhookHttpTransport"/>.
/// </summary>
public sealed class DefaultWebhookHttpResponseClassifier : IWebhookHttpResponseClassifier
{
    /// <inheritdoc />
    public WebhookDeliveryOutcome Classify(int statusCode)
    {
        if (statusCode is >= 200 and <= 299)
        {
            return WebhookDeliveryOutcome.Success;
        }

        if (statusCode is 408 or 425 or 429 || statusCode is >= 500 and <= 599)
        {
            return WebhookDeliveryOutcome.RetryableFailure;
        }

        return WebhookDeliveryOutcome.PermanentFailure;
    }
}
