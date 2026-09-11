namespace ReliableWebhooks;

/// <summary>
/// Identifies transport-level failures that occurred before a usable HTTP response was received.
/// </summary>
public enum WebhookTransportFailureKind
{
    /// <summary>
    /// No transport-level failure occurred.
    /// </summary>
    None = 0,

    /// <summary>
    /// A network or connection failure prevented a usable HTTP response from being received.
    /// </summary>
    Network = 1,

    /// <summary>
    /// The delivery attempt exceeded its configured timeout before a usable HTTP response was received.
    /// </summary>
    Timeout = 2,
}
