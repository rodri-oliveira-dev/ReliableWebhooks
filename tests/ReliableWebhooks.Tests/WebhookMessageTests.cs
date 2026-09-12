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
    [InlineData("webhook\r123")]
    [InlineData("webhook\n123")]
    [InlineData("webhook\0123")]
    [InlineData("webhook\u0001123")]
    [InlineData("webhook\u007f123")]
    public void ConstructorRejectsIdentifierWithControlCharacters(string id)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => CreateMessage(id: id));

        Assert.Equal("id", exception.ParamName);
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
    [InlineData("order\rcreated")]
    [InlineData("order\ncreated")]
    [InlineData("order\0created")]
    [InlineData("order\u0001created")]
    [InlineData("order\u007fcreated")]
    public void ConstructorRejectsEventTypeWithControlCharacters(string eventType)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => CreateMessage(eventType: eventType));

        Assert.Equal("eventType", exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ConstructorRejectsInvalidContentType(string? contentType)
    {
        Assert.ThrowsAny<ArgumentException>(() => CreateMessage(contentType: contentType!));
    }

    [Theory]
    [InlineData("application")]
    [InlineData("application/")]
    [InlineData("application json")]
    [InlineData("application/json; charset=\"unterminated")]
    [InlineData("application/\0json")]
    public void ConstructorRejectsMalformedContentType(string contentType)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => CreateMessage(contentType: contentType));

        Assert.Equal("contentType", exception.ParamName);
    }

    [Fact]
    public void ConstructorAcceptsStableIdUnicodeEventTypeAndVendorMediaType()
    {
        WebhookMessage message = CreateMessage(
            id: "order_01HRDB5TK7A9Z9",
            eventType: "pedido.criado",
            contentType: "application/vnd.reliable.order+json; charset=utf-8");

        Assert.Equal("order_01HRDB5TK7A9Z9", message.Id);
        Assert.Equal("pedido.criado", message.EventType);
        Assert.Equal("application/vnd.reliable.order+json; charset=utf-8", message.ContentType);
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

    [Theory]
    [InlineData("Bad Header")]
    [InlineData("Bad:Header")]
    [InlineData("Bad(Header)")]
    [InlineData("Bad\tHeader")]
    public void ConstructorRejectsInvalidHeaderName(string headerName)
    {
        Dictionary<string, string> headers = new()
        {
            [headerName] = "value",
        };

        Assert.Throws<ArgumentException>(() => CreateMessage(headers: headers));
    }

    [Theory]
    [InlineData("first\rsecond")]
    [InlineData("first\nsecond")]
    [InlineData("first\0second")]
    [InlineData("first\u0001second")]
    [InlineData("first\u007fsecond")]
    public void ConstructorRejectsHeaderValuesWithControlCharacters(string headerValue)
    {
        Dictionary<string, string> headers = new()
        {
            ["X-Test"] = headerValue,
        };

        Assert.Throws<ArgumentException>(() => CreateMessage(headers: headers));
    }

    [Fact]
    public void ConstructorRejectsDuplicateHeadersUsingCaseInsensitiveComparison()
    {
        Dictionary<string, string> headers = new()
        {
            ["X-Test"] = "first",
            ["x-test"] = "second",
        };

        Assert.Throws<ArgumentException>(() => CreateMessage(headers: headers));
    }

    [Theory]
    [InlineData("Connection")]
    [InlineData("Content-Length")]
    [InlineData("Expect")]
    [InlineData("Host")]
    [InlineData("Keep-Alive")]
    [InlineData("Proxy-Authenticate")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Proxy-Connection")]
    [InlineData("TE")]
    [InlineData("Trailer")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Upgrade")]
    [InlineData("host")]
    [InlineData("tRaNsFeR-eNcOdInG")]
    public void ConstructorRejectsReservedCustomHeadersUsingCaseInsensitiveComparison(string headerName)
    {
        Dictionary<string, string> headers = new()
        {
            [headerName] = "value",
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => CreateMessage(headers: headers));

        Assert.Equal("headers", exception.ParamName);
        Assert.Contains("reserved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConstructorAcceptsAuthorizationHeader()
    {
        WebhookMessage message = CreateMessage(
            headers: new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer test-token",
            });

        Assert.Equal("Bearer test-token", message.Headers["authorization"]);
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
