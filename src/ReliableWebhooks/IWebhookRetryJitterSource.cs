namespace ReliableWebhooks;

/// <summary>
/// Supplies normalized random values used to calculate retry jitter.
/// </summary>
/// <remarks>
/// Implementations must return values in the inclusive range from zero to one and should be safe for concurrent use.
/// </remarks>
public interface IWebhookRetryJitterSource
{
    /// <summary>
    /// Returns the next normalized jitter value.
    /// </summary>
    /// <returns>A value from zero through one.</returns>
    double NextValue();
}
