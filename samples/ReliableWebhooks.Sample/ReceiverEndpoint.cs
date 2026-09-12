using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace ReliableWebhooks.Sample;

internal static class ReceiverEndpoint
{
    private static readonly TimeSpan ReplayTolerance = TimeSpan.FromMinutes(5);

    public static async Task<IResult> HandleAsync(
        string behavior,
        HttpRequest request,
        SampleSigningSecret signingSecret,
        ReceiverState receiverState,
        CancellationToken cancellationToken)
    {
        byte[] payload = await ReadPayloadAsync(request, cancellationToken).ConfigureAwait(false);

        if (!VerifySignature(request.Headers, payload, signingSecret.Value.Span))
        {
            return Results.Unauthorized();
        }

        string headerWebhookId = request.Headers["X-Webhook-Id"].ToString();
        string? signedWebhookId = GetSignedWebhookId(payload);

        if (signedWebhookId is null
            || !string.Equals(headerWebhookId, signedWebhookId, StringComparison.Ordinal))
        {
            return Results.BadRequest();
        }

        int receiverAttempt = receiverState.RegisterAttempt(signedWebhookId);

        return behavior switch
        {
            "success" => Results.NoContent(),
            "retry" when receiverAttempt == 1 => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            "retry" => Results.NoContent(),
            "dead-letter" => Results.StatusCode(StatusCodes.Status503ServiceUnavailable),
            _ => Results.NotFound(),
        };
    }

    internal static bool VerifySignature(
        IHeaderDictionary headers,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> secret)
    {
        string timestampText = headers["X-Webhook-Timestamp"].ToString();
        string signatureText = headers["X-Webhook-Signature"].ToString();

        if (!long.TryParse(
                timestampText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long unixTimestamp)
            || !signatureText.StartsWith("v1=", StringComparison.Ordinal))
        {
            return false;
        }

        DateTimeOffset signedAt;

        try
        {
            signedAt = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        if ((DateTimeOffset.UtcNow - signedAt).Duration() > ReplayTolerance)
        {
            return false;
        }

        byte[] suppliedDigest;

        try
        {
            suppliedDigest = Convert.FromHexString(signatureText[3..]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] key = secret.ToArray();
        byte[] prefix = Encoding.UTF8.GetBytes(string.Concat(timestampText, "."));

        try
        {
            using IncrementalHash hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hmac.AppendData(prefix);
            hmac.AppendData(payload);
            byte[] expectedDigest = hmac.GetHashAndReset();

            return expectedDigest.Length == suppliedDigest.Length
                && CryptographicOperations.FixedTimeEquals(expectedDigest, suppliedDigest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static string? GetSignedWebhookId(ReadOnlySpan<byte> payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("WebhookId", out JsonElement webhookId)
                ? webhookId.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadPayloadAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        await request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}

internal sealed class ReceiverState
{
    private readonly ConcurrentDictionary<string, int> attempts = new(StringComparer.Ordinal);

    public int RegisterAttempt(string webhookId)
    {
        return attempts.AddOrUpdate(webhookId, 1, static (_, current) => current + 1);
    }
}
