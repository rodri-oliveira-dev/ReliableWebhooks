using ReliableWebhooks;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class WebhookMessageLimitsTests
{
    private static readonly Uri ValidDestination = new("https://example.test/webhooks");

    [Fact]
    public void ValidateAllowsMessageAtExactConfiguredBoundaries()
    {
        WebhookMessage message = CreateMessage(
            id: "abc",
            eventType: "event",
            payload: new byte[] { 1, 2, 3 },
            contentType: "text/plain",
            headers: new Dictionary<string, string>
            {
                ["A"] = "B",
                ["C"] = "D",
            });
        WebhookMessageLimits limits = new()
        {
            MaxPayloadBytes = 3,
            MaxCustomHeaders = 2,
            MaxCustomHeaderBytes = 4,
            MaxIdCharacters = 3,
            MaxEventTypeCharacters = 5,
            MaxContentTypeCharacters = 10,
            MaxDestinationUriCharacters = ValidDestination.AbsoluteUri.Length,
        };

        limits.Validate(message);
    }

    [Fact]
    public void ValidateRejectsPayloadBoundaryPlusOne()
    {
        WebhookMessage message = CreateMessage(payload: new byte[] { 1, 2, 3, 4 });
        WebhookMessageLimits limits = new()
        {
            MaxPayloadBytes = 3,
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => limits.Validate(message));

        Assert.Equal("message", exception.ParamName);
        Assert.Contains("payload", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsVeryLargePayloadWithDefaultLimit()
    {
        WebhookMessage message = CreateMessage(
            payload: new byte[WebhookMessageLimits.DefaultMaxPayloadBytes + 1]);
        WebhookMessageLimits limits = new();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => limits.Validate(message));

        Assert.Equal("message", exception.ParamName);
        Assert.Contains("payload", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsExcessiveHeaderCount()
    {
        WebhookMessage message = CreateMessage(
            headers: new Dictionary<string, string>
            {
                ["X-One"] = "1",
                ["X-Two"] = "2",
            });
        WebhookMessageLimits limits = new()
        {
            MaxCustomHeaders = 1,
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => limits.Validate(message));

        Assert.Equal("message", exception.ParamName);
        Assert.Contains("custom header count", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateRejectsExcessiveAggregateHeaderBytes()
    {
        WebhookMessage message = CreateMessage(
            headers: new Dictionary<string, string>
            {
                ["X-Test"] = "abcd",
            });
        WebhookMessageLimits limits = new()
        {
            MaxCustomHeaderBytes = 9,
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => limits.Validate(message));

        Assert.Equal("message", exception.ParamName);
        Assert.Contains("aggregate custom header bytes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("eventType")]
    [InlineData("contentType")]
    [InlineData("destination")]
    public void ValidateRejectsOversizedMetadata(string field)
    {
        WebhookMessage message = field switch
        {
            "id" => CreateMessage(id: "webhook-123"),
            "eventType" => CreateMessage(eventType: "order.created"),
            "contentType" => CreateMessage(contentType: "application/json"),
            "destination" => CreateMessage(destination: new Uri("https://example.test/webhooks/long")),
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        WebhookMessageLimits limits = new()
        {
            MaxIdCharacters = field == "id" ? 1 : WebhookMessageLimits.DefaultMaxIdCharacters,
            MaxEventTypeCharacters = field == "eventType" ? 1 : WebhookMessageLimits.DefaultMaxEventTypeCharacters,
            MaxContentTypeCharacters = field == "contentType" ? 1 : WebhookMessageLimits.DefaultMaxContentTypeCharacters,
            MaxDestinationUriCharacters = field == "destination" ? ValidDestination.AbsoluteUri.Length : WebhookMessageLimits.DefaultMaxDestinationUriCharacters,
        };

        ArgumentException exception = Assert.Throws<ArgumentException>(() => limits.Validate(message));

        Assert.Equal("message", exception.ParamName);
    }

    private static WebhookMessage CreateMessage(
        string id = "webhook-123",
        string eventType = "order.created",
        Uri? destination = null,
        byte[]? payload = null,
        string contentType = "application/json",
        IReadOnlyDictionary<string, string>? headers = null)
    {
        return new WebhookMessage(
            id,
            eventType,
            destination ?? ValidDestination,
            payload ?? new byte[] { 1, 2, 3 },
            contentType,
            headers);
    }
}
