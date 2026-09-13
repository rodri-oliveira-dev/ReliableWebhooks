namespace ReliableWebhooks;

/// <summary>
/// Represents the authorization result returned by an <see cref="IWebhookDestinationPolicy"/>.
/// </summary>
public sealed class WebhookDestinationPolicyResult
{
    private WebhookDestinationPolicyResult(bool isAllowed)
    {
        IsAllowed = isAllowed;
    }

    /// <summary>
    /// Gets a value indicating whether the destination is authorized.
    /// </summary>
    public bool IsAllowed
    {
        get;
    }

    /// <summary>
    /// Returns a destination policy result that authorizes the destination.
    /// </summary>
    /// <returns>An allowed result.</returns>
    public static WebhookDestinationPolicyResult Allow()
    {
        return new WebhookDestinationPolicyResult(true);
    }

    /// <summary>
    /// Returns a destination policy result that denies the destination.
    /// </summary>
    /// <returns>A denied result.</returns>
    public static WebhookDestinationPolicyResult Deny()
    {
        return new WebhookDestinationPolicyResult(false);
    }
}
