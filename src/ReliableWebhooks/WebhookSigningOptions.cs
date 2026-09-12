namespace ReliableWebhooks;

/// <summary>
/// Configures signed webhook delivery headers and timestamp generation.
/// </summary>
public sealed class WebhookSigningOptions
{
    /// <summary>
    /// Gets the header name used for the stable webhook identifier.
    /// </summary>
    public string WebhookIdHeaderName
    {
        get;
        init;
    } = "X-Webhook-Id";

    /// <summary>
    /// Gets the header name used for the webhook event type.
    /// </summary>
    public string EventTypeHeaderName
    {
        get;
        init;
    } = "X-Webhook-Event";

    /// <summary>
    /// Gets the header name used for the Unix timestamp in seconds.
    /// </summary>
    public string TimestampHeaderName
    {
        get;
        init;
    } = "X-Webhook-Timestamp";

    /// <summary>
    /// Gets the header name used for the webhook signature.
    /// </summary>
    public string SignatureHeaderName
    {
        get;
        init;
    } = "X-Webhook-Signature";

    /// <summary>
    /// Gets the time provider used to create signing timestamps.
    /// </summary>
    public TimeProvider TimeProvider
    {
        get;
        init;
    } = TimeProvider.System;
}
