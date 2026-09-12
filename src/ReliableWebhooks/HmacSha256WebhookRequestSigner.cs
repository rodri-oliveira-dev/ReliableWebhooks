using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReliableWebhooks;

/// <summary>
/// Signs outbound webhook payloads with HMAC-SHA256.
/// </summary>
/// <remarks>
/// The <c>v1</c> canonical representation starts with the ASCII format marker
/// <c>rw-hmac-sha256/v1\0</c>, followed by length-prefixed frames for the UTC Unix timestamp in seconds,
/// webhook ID, event type, content type, and exact payload bytes sent in the HTTP request. String frames are
/// UTF-8 encoded. Each frame is prefixed with an eight-byte big-endian length. The returned header value uses
/// the format <c>v1=&lt;lowercase-hex-digest&gt;</c>.
/// </remarks>
public sealed class HmacSha256WebhookRequestSigner : IWebhookRequestSigner
{
    private const int MinimumSecretBytes = 32;

    private readonly IWebhookSigningSecretProvider secretProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="HmacSha256WebhookRequestSigner"/> class.
    /// </summary>
    /// <param name="secretProvider">The provider used to resolve signing secret material.</param>
    /// <exception cref="ArgumentNullException"><paramref name="secretProvider"/> is <see langword="null"/>.</exception>
    public HmacSha256WebhookRequestSigner(IWebhookSigningSecretProvider secretProvider)
    {
        ArgumentNullException.ThrowIfNull(secretProvider);
        this.secretProvider = secretProvider;
    }

    /// <inheritdoc />
    public async ValueTask<string> SignAsync(
        WebhookMessage message,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        ReadOnlyMemory<byte> secret = await secretProvider
            .GetSecretAsync(message, cancellationToken)
            .ConfigureAwait(false);

        if (secret.Length < MinimumSecretBytes)
        {
            throw new InvalidOperationException(
                "The webhook signing secret must contain at least 32 bytes (256 bits) of cryptographically generated key material.");
        }

        string timestampText = timestamp
            .ToUniversalTime()
            .ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        byte[] key = secret.ToArray();

        try
        {
            using IncrementalHash hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hmac.AppendData("rw-hmac-sha256/v1\0"u8);
            AppendUtf8Frame(hmac, timestampText);
            AppendUtf8Frame(hmac, message.Id);
            AppendUtf8Frame(hmac, message.EventType);
            AppendUtf8Frame(hmac, message.ContentType);
            AppendFrame(hmac, payload.Span);
            byte[] digest = hmac.GetHashAndReset();

            return string.Concat("v1=", Convert.ToHexStringLower(digest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static void AppendUtf8Frame(IncrementalHash hmac, string value)
    {
        AppendFrame(hmac, Encoding.UTF8.GetBytes(value));
    }

    private static void AppendFrame(IncrementalHash hmac, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(length, value.Length);
        hmac.AppendData(length);
        hmac.AppendData(value);
    }
}
