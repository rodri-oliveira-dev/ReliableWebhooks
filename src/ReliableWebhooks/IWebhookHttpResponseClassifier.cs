namespace ReliableWebhooks;

/// <summary>
/// Classifies HTTP status codes into transport-neutral webhook delivery outcomes.
/// </summary>
public interface IWebhookHttpResponseClassifier
{
    /// <summary>
    /// Classifies an HTTP response status code.
    /// </summary>
    /// <param name="statusCode">The numeric HTTP response status code.</param>
    /// <returns>The delivery outcome associated with <paramref name="statusCode"/>.</returns>
    WebhookDeliveryOutcome Classify(int statusCode);
}
