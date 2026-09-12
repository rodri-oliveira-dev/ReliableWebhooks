using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ReliableWebhooks;

/// <summary>
/// Signs outbound webhook payloads with HMAC-SHA256.
/// </summary>
/// <remarks>
/// The canonical representation is the UTF-8 Unix timestamp in seconds, followed by a period (<c>.</c>),
/// followed by the exact payload bytes sent in the HTTP request. The returned header value uses the format
/// <c>v1=&lt;lowercase-hex-digest&gt;</c>.
/// </remarks>
public sealed class HmacSha256WebhookRequestSigner : IWebhookRequestSigner
{
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

        if (secret.IsEmpty)
        {
            throw new InvalidOperationException("The webhook signing secret cannot be empty.");
        }

        string timestampText = timestamp
            .ToUniversalTime()
            .ToUnixTimeSeconds()
            .ToString(CultureInfo.InvariantCulture);
        byte[] prefix = Encoding.UTF8.GetBytes(string.Concat(timestampText, "."));
        byte[] key = secret.ToArray();

        try
        {
            using IncrementalHash hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hmac.AppendData(prefix);
            hmac.AppendData(payload.Span);
            byte[] digest = hmac.GetHashAndReset();

            return string.Concat("v1=", Convert.ToHexStringLower(digest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
