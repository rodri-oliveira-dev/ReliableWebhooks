namespace ReliableWebhooks;

/// <summary>
/// Calculates retry schedules using capped exponential backoff, bounded positive jitter, and Retry-After metadata.
/// </summary>
public sealed class DefaultWebhookRetryPolicy : IWebhookRetryPolicy
{
    private readonly int maxAttempts;
    private readonly TimeSpan baseDelay;
    private readonly TimeSpan maxDelay;
    private readonly double jitterFactor;
    private readonly IWebhookRetryJitterSource jitterSource;

    /// <summary>
    /// Initializes a new default retry policy.
    /// </summary>
    /// <param name="options">Optional retry configuration.</param>
    /// <param name="jitterSource">Optional normalized jitter source. Random shared values are used by default.</param>
    /// <exception cref="ArgumentOutOfRangeException">A retry option is outside its supported range.</exception>
    public DefaultWebhookRetryPolicy(
        WebhookRetryPolicyOptions? options = null,
        IWebhookRetryJitterSource? jitterSource = null)
    {
        options ??= new WebhookRetryPolicyOptions();

        if (options.MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxAttempts,
                "Maximum attempts must be greater than zero.");
        }

        if (options.BaseDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.BaseDelay,
                "Base delay must be greater than zero.");
        }

        if (options.MaxDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxDelay,
                "Maximum delay must be greater than zero.");
        }

        if (options.MaxDelay < options.BaseDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxDelay,
                "Maximum delay cannot be shorter than the base delay.");
        }

        if (!double.IsFinite(options.JitterFactor)
            || options.JitterFactor < 0
            || options.JitterFactor > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.JitterFactor,
                "Jitter factor must be a finite value from zero through one.");
        }

        maxAttempts = options.MaxAttempts;
        baseDelay = options.BaseDelay;
        maxDelay = options.MaxDelay;
        jitterFactor = options.JitterFactor;
        this.jitterSource = jitterSource ?? SharedRandomJitterSource.Instance;
    }

    /// <inheritdoc />
    public WebhookRetryDecision GetDecision(WebhookRetryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        int attemptCount = context.Delivery.AttemptCount;
        if (attemptCount < 1)
        {
            throw new InvalidOperationException("Retry scheduling requires a delivery with at least one started attempt.");
        }

        if (attemptCount >= maxAttempts)
        {
            return new WebhookRetryDecision(WebhookRetryAction.DeadLetter, null);
        }

        TimeSpan localDelay = CalculateLocalDelay(attemptCount);
        TimeSpan retryAfterDelay = CalculateRetryAfterDelay(context);
        TimeSpan effectiveDelay = localDelay >= retryAfterDelay ? localDelay : retryAfterDelay;

        if (effectiveDelay > maxDelay)
        {
            effectiveDelay = maxDelay;
        }

        DateTimeOffset nextAttemptAt = AddDelay(context.Now, effectiveDelay);
        return new WebhookRetryDecision(WebhookRetryAction.Retry, nextAttemptAt);
    }

    private TimeSpan CalculateLocalDelay(int attemptCount)
    {
        double exponentialTicks = baseDelay.Ticks * Math.Pow(2, attemptCount - 1);
        long boundedTicks = !double.IsFinite(exponentialTicks) || exponentialTicks >= maxDelay.Ticks
            ? maxDelay.Ticks
            : (long)exponentialTicks;

        if (jitterFactor == 0 || boundedTicks >= maxDelay.Ticks)
        {
            return TimeSpan.FromTicks(boundedTicks);
        }

        double normalizedJitter = jitterSource.NextValue();
        if (!double.IsFinite(normalizedJitter) || normalizedJitter < 0 || normalizedJitter > 1)
        {
            throw new InvalidOperationException("The retry jitter source returned a value outside the range from zero through one.");
        }

        double jitterTicks = boundedTicks * jitterFactor * normalizedJitter;
        double jitteredTicks = boundedTicks + jitterTicks;
        long finalTicks = jitteredTicks >= maxDelay.Ticks
            ? maxDelay.Ticks
            : (long)Math.Round(jitteredTicks, MidpointRounding.AwayFromZero);

        return TimeSpan.FromTicks(finalTicks);
    }

    private static TimeSpan CalculateRetryAfterDelay(WebhookRetryContext context)
    {
        TimeSpan retryAfterDelay = TimeSpan.Zero;

        if (context.RetryAfterDelay is TimeSpan delta && delta > TimeSpan.Zero)
        {
            retryAfterDelay = delta;
        }

        if (context.RetryAfterDate is DateTimeOffset retryAfterDate && retryAfterDate > context.Now)
        {
            TimeSpan dateDelay = retryAfterDate - context.Now;
            if (dateDelay > retryAfterDelay)
            {
                retryAfterDelay = dateDelay;
            }
        }

        return retryAfterDelay;
    }

    private static DateTimeOffset AddDelay(DateTimeOffset now, TimeSpan delay)
    {
        try
        {
            return now.Add(delay);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MaxValue;
        }
    }

    private sealed class SharedRandomJitterSource : IWebhookRetryJitterSource
    {
        internal static readonly SharedRandomJitterSource Instance = new();

        private SharedRandomJitterSource()
        {
        }

        public double NextValue()
        {
            return Random.Shared.NextDouble();
        }
    }
}
