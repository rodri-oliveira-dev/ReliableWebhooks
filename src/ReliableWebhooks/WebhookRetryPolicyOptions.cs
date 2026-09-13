namespace ReliableWebhooks;

/// <summary>
/// Configures the default webhook retry scheduling policy.
/// </summary>
public sealed class WebhookRetryPolicyOptions
{
    /// <summary>
    /// Gets the maximum number of delivery attempts, including the current attempt.
    /// </summary>
    public int MaxAttempts
    {
        get;
        init;
    } = 5;

    /// <summary>
    /// Gets the exponential-backoff delay used after the first failed attempt.
    /// </summary>
    public TimeSpan BaseDelay
    {
        get;
        init;
    } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the maximum delay allowed for locally generated exponential backoff and jitter.
    /// </summary>
    public TimeSpan MaxDelay
    {
        get;
        init;
    } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the maximum positive jitter as a fraction of the calculated exponential delay.
    /// </summary>
    /// <remarks>
    /// A value of 0.2 allows adding between zero and twenty percent of the exponential delay before
    /// the final delay is capped by <see cref="MaxDelay"/>.
    /// </remarks>
    public double JitterFactor
    {
        get;
        init;
    } = 0.2;
}
