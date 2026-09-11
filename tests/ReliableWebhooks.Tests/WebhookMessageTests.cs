using System.Runtime.InteropServices;
using ReliableWebhooks;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class WebhookMessageTests
{
    private static readonly Uri ValidDestination = new("https://example.test/webhooks");

    [Fact]
    public void ConstructorCapturesImmutablePayloadAndHeaderSnapshots()
    {
        byte[] payload = [1, 2, 3];
        Dictionary<string, string> headers = new()
        {
            ["X-Correlation-Id"] = "original",
        };

        WebhookMessage message = new(
            "webhook-123",
            "order.created",
            ValidDestination,
            payload,
            "application/json",
            headers);

        payload[0] = 9;
        headers["X-Correlation-Id"] = "changed";

        Assert.Equal(new byte[] { 1, 2, 3 }, message.Payload.ToArray());
        Assert.Equal("original", message.Headers["x-correlation-id"]);
    }

    [Fact]
    public void PayloadDoesNotExposeTheInternalBackingArray()
    {
        WebhookMessage message = CreateMessage();
        ReadOnlyMemory<byte> exposedPayload = message.Payload;

        Assert.True(MemoryMarshal.TryGetArray(exposedPayload, out ArraySegment<byte> segment));
        segment.Array![segment.Offset] = 9;

        Assert.Equal(new byte[] { 1, 2, 3 }, message.Payload.ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConstructorRejectsInvalidIdentifier(string? id)
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateMessage(id: id!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConstructorRejectsInvalidEventType(string? eventType)
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateMessage(eventType: eventType!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConstructorRejectsInvalidContentType(string? contentType)
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateMessage(contentType: contentType!));
    }

    [Fact]
    public void ConstructorRejectsNullDestination()
    {
        Assert.Throws<ArgumentNullException>(
            () => new WebhookMessage(
                "webhook-123",
                "order.created",
                null!,
                new byte[] { 1, 2, 3 },
                "application/json"));
    }

    [Theory]
    [InlineData("/relative")]
    [InlineData("ftp://example.test/webhooks")]
    public void ConstructorRejectsUnsupportedDestination(string destination)
    {
        Uri uri = new(destination, UriKind.RelativeOrAbsolute);

        Assert.Throws<ArgumentException>(() => CreateMessage(destination: uri));
    }

    [Fact]
    public void ConstructorRejectsBlankHeaderName()
    {
        Dictionary<string, string> headers = new()
        {
            [" "] = "value",
        };

        Assert.Throws<ArgumentException>(() => CreateMessage(headers: headers));
    }

    private static WebhookMessage CreateMessage(
        string id = "webhook-123",
        string eventType = "order.created",
        Uri? destination = null,
        string contentType = "application/json",
        IReadOnlyDictionary<string, string>? headers = null)
    {
        return new WebhookMessage(
            id,
            eventType,
            destination ?? ValidDestination,
            new byte[] { 1, 2, 3 },
            contentType,
            headers);
    }
}
