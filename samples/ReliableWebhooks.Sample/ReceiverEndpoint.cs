using System.Buffers.Binary;
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

        if (!VerifySignature(request.Headers, request.ContentType, payload, signingSecret.Value.Span))
        {
            return Results.Unauthorized();
        }

        string headerWebhookId = request.Headers["X-Webhook-Id"].ToString();
        string headerEventType = request.Headers["X-Webhook-Event"].ToString();
        SignedPayload? signedPayload = ParseSignedPayload(payload);

        if (signedPayload is null
            || !string.Equals(headerWebhookId, signedPayload.WebhookId, StringComparison.Ordinal)
            || !string.Equals(headerEventType, $"sample.{signedPayload.Behavior}", StringComparison.Ordinal)
            || !string.Equals(behavior, signedPayload.Behavior, StringComparison.Ordinal))
        {
            return Results.BadRequest();
        }

        int receiverAttempt = receiverState.RegisterAttempt(signedPayload.WebhookId);

        return signedPayload.Behavior switch
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
        string? contentType,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> secret)
    {
        string webhookId = headers["X-Webhook-Id"].ToString();
        string eventType = headers["X-Webhook-Event"].ToString();
        string timestampText = headers["X-Webhook-Timestamp"].ToString();
        string signatureText = headers["X-Webhook-Signature"].ToString();

        if (string.IsNullOrWhiteSpace(webhookId)
            || string.IsNullOrWhiteSpace(eventType)
            || string.IsNullOrWhiteSpace(contentType)
            || !long.TryParse(
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

        try
        {
            using IncrementalHash hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            hmac.AppendData("rw-hmac-sha256/v1\0"u8);
            AppendUtf8Frame(hmac, timestampText);
            AppendUtf8Frame(hmac, webhookId);
            AppendUtf8Frame(hmac, eventType);
            AppendUtf8Frame(hmac, contentType);
            AppendFrame(hmac, payload);
            byte[] expectedDigest = hmac.GetHashAndReset();

            return expectedDigest.Length == suppliedDigest.Length
                && CryptographicOperations.FixedTimeEquals(expectedDigest, suppliedDigest);
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

    private static SignedPayload? ParseSignedPayload(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("WebhookId", out JsonElement webhookIdElement)
                || !root.TryGetProperty("Behavior", out JsonElement behaviorElement))
            {
                return null;
            }

            string? webhookId = webhookIdElement.GetString();
            string? signedBehavior = behaviorElement.GetString();

            return string.IsNullOrWhiteSpace(webhookId) || string.IsNullOrWhiteSpace(signedBehavior)
                ? null
                : new SignedPayload(webhookId, signedBehavior);
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

    private sealed record SignedPayload(string WebhookId, string Behavior);
}

internal sealed class ReceiverState
{
    private readonly ConcurrentDictionary<string, int> attempts = new(StringComparer.Ordinal);

    public int RegisterAttempt(string webhookId)
    {
        return attempts.AddOrUpdate(webhookId, 1, static (_, current) => current + 1);
    }
}
