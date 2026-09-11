namespace ReliableWebhooks;

/// <summary>
/// Contains the persisted delivery state and response metadata used to calculate a retry decision.
/// </summary>
public sealed class WebhookRetryContext
{
    /// <summary>
    /// Initializes a new retry context.
    /// </summary>
    /// <param name="delivery">The current persisted delivery snapshot.</param>
    /// <param name="now">The reference time used to calculate the next attempt.</param>
    /// <param name="retryAfterDelay">The delta-form Retry-After value, when available.</param>
    /// <param name="retryAfterDate">The date-form Retry-After value, when available.</param>
    /// <exception cref="ArgumentNullException"><paramref name="delivery"/> is <see langword="null"/>.</exception>
    public WebhookRetryContext(
        WebhookDeliverySnapshot delivery,
        DateTimeOffset now,
        TimeSpan? retryAfterDelay = null,
        DateTimeOffset? retryAfterDate = null)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        Delivery = delivery;
        Now = now;
        RetryAfterDelay = retryAfterDelay;
        RetryAfterDate = retryAfterDate;
    }

    /// <summary>
    /// Creates a retry context from a transport result.
    /// </summary>
    /// <param name="delivery">The current persisted delivery snapshot.</param>
    /// <param name="result">The transport result that triggered retry scheduling.</param>
    /// <param name="now">The reference time used to calculate the next attempt.</param>
    /// <returns>A retry context containing the transport Retry-After metadata.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="delivery"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    public static WebhookRetryContext FromResult(
        WebhookDeliverySnapshot delivery,
        WebhookDeliveryResult result,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(result);

        return new WebhookRetryContext(
            delivery,
            now,
            result.RetryAfterDelay,
            result.RetryAfterDate);
    }

    /// <summary>
    /// Gets the current persisted delivery snapshot.
    /// </summary>
    public WebhookDeliverySnapshot Delivery
    {
        get;
    }

    /// <summary>
    /// Gets the reference time used to calculate the next attempt.
    /// </summary>
    public DateTimeOffset Now
    {
        get;
    }

    /// <summary>
    /// Gets the delta-form Retry-After value, when available.
    /// </summary>
    public TimeSpan? RetryAfterDelay
    {
        get;
    }

    /// <summary>
    /// Gets the date-form Retry-After value, when available.
    /// </summary>
    public DateTimeOffset? RetryAfterDate
    {
        get;
    }
}
