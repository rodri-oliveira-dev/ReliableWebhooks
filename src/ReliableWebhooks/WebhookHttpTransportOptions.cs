namespace ReliableWebhooks;

/// <summary>
/// Configures the behavior of <see cref="WebhookHttpTransport"/>.
/// </summary>
public sealed class WebhookHttpTransportOptions
{
    /// <summary>
    /// Gets the maximum duration of one HTTP delivery attempt.
    /// </summary>
    /// <remarks>
    /// The default is 30 seconds. Set this value to <see cref="Timeout.InfiniteTimeSpan"/> to disable
    /// the transport-level timeout. The effective timeout may still be shorter when the supplied
    /// <see cref="HttpClient"/> has a shorter <see cref="HttpClient.Timeout"/>.
    /// </remarks>
    public TimeSpan AttemptTimeout
    {
        get;
        init;
    } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the maximum number of response-body bytes retained in a delivery result.
    /// </summary>
    /// <remarks>
    /// The default is 16 KiB. The transport uses response-headers-read mode and reads at most this
    /// many bytes plus one probe byte used only to detect truncation. The probe byte is never retained.
    /// A value of zero disables response-body retention while still detecting whether a body exists.
    /// </remarks>
    public int MaxResponseBodyBytes
    {
        get;
        init;
    } = 16 * 1024;
}
