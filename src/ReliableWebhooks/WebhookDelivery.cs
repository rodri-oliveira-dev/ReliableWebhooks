using System.Collections.ObjectModel;

namespace ReliableWebhooks;

/// <summary>
/// Represents an immutable snapshot of a webhook and its delivery lifecycle information.
/// </summary>
public sealed class WebhookDelivery
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WebhookDelivery"/> class.
    /// </summary>
    /// <param name="message">The webhook message being delivered.</param>
    /// <param name="state">The current delivery state.</param>
    /// <param name="attempts">Previously recorded delivery attempts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is not a defined <see cref="DeliveryState"/> value.</exception>
    public WebhookDelivery(
        WebhookMessage message,
        DeliveryState state = DeliveryState.Pending,
        IEnumerable<DeliveryAttempt>? attempts = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Delivery state must be a defined value.");
        }

        Message = message;
        State = state;
        Attempts = new ReadOnlyCollection<DeliveryAttempt>(attempts?.ToArray() ?? []);
    }

    /// <summary>
    /// Gets the webhook message.
    /// </summary>
    public WebhookMessage Message { get; }

    /// <summary>
    /// Gets the current delivery state.
    /// </summary>
    public DeliveryState State { get; }

    /// <summary>
    /// Gets an immutable snapshot of recorded delivery attempts.
    /// </summary>
    public IReadOnlyList<DeliveryAttempt> Attempts { get; }
}
