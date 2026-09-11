namespace ReliableWebhooks;

/// <summary>
/// Identifies the action selected by a webhook retry policy.
/// </summary>
public enum WebhookRetryAction
{
    /// <summary>
    /// Schedule another delivery attempt.
    /// </summary>
    Retry = 0,

    /// <summary>
    /// Stop automatic retries and transition the delivery to dead-letter state.
    /// </summary>
    DeadLetter = 1,
}

/// <summary>
/// Represents the scheduling decision returned by a webhook retry policy.
/// </summary>
public sealed class WebhookRetryDecision
{
    /// <summary>
    /// Initializes a new retry decision.
    /// </summary>
    /// <param name="action">The selected retry action.</param>
    /// <param name="nextAttemptAt">The next attempt timestamp when <paramref name="action"/> is Retry.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="action"/> is undefined.</exception>
    /// <exception cref="ArgumentException">
    /// Retry is selected without a next-attempt timestamp, or DeadLetter is selected with one.
    /// </exception>
    public WebhookRetryDecision(WebhookRetryAction action, DateTimeOffset? nextAttemptAt)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action), action, "Retry action must be a defined value.");
        }

        if (action == WebhookRetryAction.Retry && nextAttemptAt is null)
        {
            throw new ArgumentException("A retry decision must include the next-attempt timestamp.", nameof(nextAttemptAt));
        }

        if (action == WebhookRetryAction.DeadLetter && nextAttemptAt is not null)
        {
            throw new ArgumentException("A dead-letter decision cannot include a next-attempt timestamp.", nameof(nextAttemptAt));
        }

        Action = action;
        NextAttemptAt = nextAttemptAt;
    }

    /// <summary>
    /// Gets the selected retry action.
    /// </summary>
    public WebhookRetryAction Action
    {
        get;
    }

    /// <summary>
    /// Gets the next attempt timestamp when another retry was selected.
    /// </summary>
    public DateTimeOffset? NextAttemptAt
    {
        get;
    }

    /// <summary>
    /// Gets a value indicating whether another delivery attempt should be scheduled.
    /// </summary>
    public bool ShouldRetry => Action == WebhookRetryAction.Retry;
}
