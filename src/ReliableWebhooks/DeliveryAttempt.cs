namespace ReliableWebhooks;

/// <summary>
/// Represents transport-neutral information about one webhook delivery attempt.
/// </summary>
public sealed class DeliveryAttempt
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeliveryAttempt"/> class.
    /// </summary>
    /// <param name="number">The one-based attempt number.</param>
    /// <param name="startedAt">The time at which the attempt started.</param>
    /// <param name="completedAt">The time at which the attempt completed, when applicable.</param>
    /// <param name="error">A transport-neutral error description, when applicable.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="number"/> is less than one, or <paramref name="completedAt"/> precedes <paramref name="startedAt"/>.
    /// </exception>
    public DeliveryAttempt(
        int number,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt = null,
        string? error = null)
    {
        if (number < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(number), number, "Attempt number must be greater than zero.");
        }

        if (completedAt < startedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                completedAt,
                "Completion time cannot precede the attempt start time.");
        }

        Number = number;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Error = error;
    }

    /// <summary>
    /// Gets the one-based attempt number.
    /// </summary>
    public int Number
    {
        get;
    }

    /// <summary>
    /// Gets the time at which the attempt started.
    /// </summary>
    public DateTimeOffset StartedAt
    {
        get;
    }

    /// <summary>
    /// Gets the time at which the attempt completed, when available.
    /// </summary>
    public DateTimeOffset? CompletedAt
    {
        get;
    }

    /// <summary>
    /// Gets the transport-neutral error description, when available.
    /// </summary>
    public string? Error
    {
        get;
    }
}
