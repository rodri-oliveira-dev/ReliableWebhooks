using System.Text;

namespace ReliableWebhooks;

/// <summary>
/// Defines resource limits for outbound webhook messages entering the reliable-delivery pipeline.
/// </summary>
public sealed class WebhookMessageLimits
{
    /// <summary>
    /// Gets the default maximum payload size, in bytes.
    /// </summary>
    public const int DefaultMaxPayloadBytes = 1024 * 1024;

    /// <summary>
    /// Gets the default maximum number of custom headers persisted with a webhook message.
    /// </summary>
    public const int DefaultMaxCustomHeaders = 32;

    /// <summary>
    /// Gets the default maximum aggregate custom-header size, in UTF-8 bytes.
    /// </summary>
    public const int DefaultMaxCustomHeaderBytes = 16 * 1024;

    /// <summary>
    /// Gets the default maximum webhook identifier length, in UTF-16 characters.
    /// </summary>
    public const int DefaultMaxIdCharacters = 128;

    /// <summary>
    /// Gets the default maximum event type length, in UTF-16 characters.
    /// </summary>
    public const int DefaultMaxEventTypeCharacters = 128;

    /// <summary>
    /// Gets the default maximum content type length, in UTF-16 characters.
    /// </summary>
    public const int DefaultMaxContentTypeCharacters = 256;

    /// <summary>
    /// Gets the default maximum destination URI length, in UTF-16 characters.
    /// </summary>
    public const int DefaultMaxDestinationUriCharacters = 2048;

    /// <summary>
    /// Gets or sets the maximum payload size, in bytes.
    /// </summary>
    /// <remarks>The default is 1 MiB. A value of zero allows only empty payloads.</remarks>
    public int MaxPayloadBytes
    {
        get;
        set;
    } = DefaultMaxPayloadBytes;

    /// <summary>
    /// Gets or sets the maximum number of custom headers persisted with a webhook message.
    /// </summary>
    /// <remarks>The default is 32. A value of zero disables persisted custom headers.</remarks>
    public int MaxCustomHeaders
    {
        get;
        set;
    } = DefaultMaxCustomHeaders;

    /// <summary>
    /// Gets or sets the maximum aggregate custom-header size, in UTF-8 bytes.
    /// </summary>
    /// <remarks>
    /// The default is 16 KiB. Header names and values both count toward the aggregate limit.
    /// A value of zero allows only an empty custom-header collection.
    /// </remarks>
    public int MaxCustomHeaderBytes
    {
        get;
        set;
    } = DefaultMaxCustomHeaderBytes;

    /// <summary>
    /// Gets or sets the maximum webhook identifier length, in UTF-16 characters.
    /// </summary>
    public int MaxIdCharacters
    {
        get;
        set;
    } = DefaultMaxIdCharacters;

    /// <summary>
    /// Gets or sets the maximum event type length, in UTF-16 characters.
    /// </summary>
    public int MaxEventTypeCharacters
    {
        get;
        set;
    } = DefaultMaxEventTypeCharacters;

    /// <summary>
    /// Gets or sets the maximum content type length, in UTF-16 characters.
    /// </summary>
    public int MaxContentTypeCharacters
    {
        get;
        set;
    } = DefaultMaxContentTypeCharacters;

    /// <summary>
    /// Gets or sets the maximum destination URI length, in UTF-16 characters.
    /// </summary>
    public int MaxDestinationUriCharacters
    {
        get;
        set;
    } = DefaultMaxDestinationUriCharacters;

    /// <summary>
    /// Validates <paramref name="message"/> against the configured limits.
    /// </summary>
    /// <param name="message">The webhook message to validate before persistence or dispatch.</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The message exceeds one of the configured limits.</exception>
    public void Validate(WebhookMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        ThrowIfExceeded(message.PayloadBuffer.Length, MaxPayloadBytes, "payload bytes", nameof(message));
        ThrowIfExceeded(message.Headers.Count, MaxCustomHeaders, "custom header count", nameof(message));
        ThrowIfExceeded(
            GetAggregateHeaderBytes(message.Headers),
            MaxCustomHeaderBytes,
            "aggregate custom header bytes",
            nameof(message));
        ThrowIfExceeded(message.Id.Length, MaxIdCharacters, "webhook ID characters", nameof(message));
        ThrowIfExceeded(message.EventType.Length, MaxEventTypeCharacters, "event type characters", nameof(message));
        ThrowIfExceeded(message.ContentType.Length, MaxContentTypeCharacters, "content type characters", nameof(message));
        ThrowIfExceeded(
            message.Destination.AbsoluteUri.Length,
            MaxDestinationUriCharacters,
            "destination URI characters",
            nameof(message));
    }

    private static int GetAggregateHeaderBytes(IReadOnlyDictionary<string, string> headers)
    {
        int total = 0;

        checked
        {
            foreach ((string name, string value) in headers)
            {
                total += Encoding.UTF8.GetByteCount(name);
                total += Encoding.UTF8.GetByteCount(value);
            }
        }

        return total;
    }

    private static void ThrowIfExceeded(int actual, int limit, string label, string paramName)
    {
        if (actual > limit)
        {
            throw new ArgumentException(
                $"Webhook message {label} exceeds the configured limit of {limit}.",
                paramName);
        }
    }
}
