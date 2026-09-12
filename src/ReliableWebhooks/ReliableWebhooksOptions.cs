namespace ReliableWebhooks;

/// <summary>
/// Groups the options used by the default dependency-injection integration.
/// </summary>
public sealed class ReliableWebhooksOptions
{
    /// <summary>
    /// Gets or sets dispatcher, leasing, polling, concurrency, and shutdown options.
    /// </summary>
    public WebhookDispatcherOptions Dispatcher
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Gets or sets the default retry policy options.
    /// </summary>
    public WebhookRetryPolicyOptions Retry
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Gets or sets the HTTP transport and request-signing options.
    /// </summary>
    public WebhookHttpTransportOptions Transport
    {
        get;
        set;
    } = new();
}
