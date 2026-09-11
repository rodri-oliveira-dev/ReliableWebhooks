using System.Collections.ObjectModel;

namespace ReliableWebhooks;

/// <summary>
/// Represents the immutable data required to deliver an outbound webhook.
/// </summary>
public sealed class WebhookMessage
{
    private readonly byte[] payload;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebhookMessage"/> class.
    /// </summary>
    /// <param name="id">A stable identifier used to identify the webhook across delivery attempts.</param>
    /// <param name="eventType">The application-defined event type.</param>
    /// <param name="destination">The absolute HTTP or HTTPS destination URI.</param>
    /// <param name="payload">The exact payload bytes to be delivered.</param>
    /// <param name="contentType">The payload content type.</param>
    /// <param name="headers">Optional custom delivery headers.</param>
    /// <exception cref="ArgumentException">
    /// A required string value is empty or whitespace, a header name is empty or whitespace,
    /// or <paramref name="destination"/> is not an absolute HTTP or HTTPS URI.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    public WebhookMessage(
        string id,
        string eventType,
        Uri destination,
        ReadOnlyMemory<byte> payload,
        string contentType,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        if (!destination.IsAbsoluteUri || !IsHttpDestination(destination))
        {
            throw new ArgumentException("Destination must be an absolute HTTP or HTTPS URI.", nameof(destination));
        }

        Id = id;
        EventType = eventType;
        Destination = destination;
        this.payload = payload.ToArray();
        ContentType = contentType;
        Headers = CopyHeaders(headers);
    }

    /// <summary>
    /// Gets the stable webhook identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the application-defined event type.
    /// </summary>
    public string EventType { get; }

    /// <summary>
    /// Gets the absolute HTTP or HTTPS destination URI.
    /// </summary>
    public Uri Destination { get; }

    /// <summary>
    /// Gets the exact payload bytes captured when this message was created.
    /// </summary>
    public ReadOnlyMemory<byte> Payload => payload;

    /// <summary>
    /// Gets the payload content type.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Gets a read-only snapshot of custom delivery headers.
    /// </summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    private static bool IsHttpDestination(Uri destination)
    {
        return string.Equals(destination.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(destination.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> CopyHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        Dictionary<string, string> copy = new(StringComparer.OrdinalIgnoreCase);

        if (headers is null)
        {
            return new ReadOnlyDictionary<string, string>(copy);
        }

        foreach ((string name, string value) in headers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(headers));

            if (value is null)
            {
                throw new ArgumentException("Header values cannot be null.", nameof(headers));
            }

            copy.Add(name, value);
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }
}
