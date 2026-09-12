using System.Net;
using System.Text;
using ReliableWebhooks;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class WebhookSigningTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task HmacSignerMatchesKnownVector()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"id\":123}");
        HmacSha256WebhookRequestSigner signer = new(
            new TestSecretProvider(Encoding.UTF8.GetBytes("whsec_test_secret")));

        string signature = await signer.SignAsync(
            CreateMessage(payload),
            payload,
            FixedTimestamp,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "v1=3b2bd7a533a3752d16ec9d722ba635dca7a04012c61e46c96892b41efb0a353b",
            signature);
    }

    [Fact]
    public async Task HmacSignerChangesWhenPayloadOrTimestampChanges()
    {
        HmacSha256WebhookRequestSigner signer = new(
            new TestSecretProvider(Encoding.UTF8.GetBytes("whsec_test_secret")));
        byte[] originalPayload = Encoding.UTF8.GetBytes("{\"id\":123}");
        byte[] mutatedPayload = Encoding.UTF8.GetBytes("{\"id\":124}");
        WebhookMessage message = CreateMessage(originalPayload);

        string original = await signer.SignAsync(
            message,
            originalPayload,
            FixedTimestamp,
            TestContext.Current.CancellationToken);
        string payloadMutation = await signer.SignAsync(
            message,
            mutatedPayload,
            FixedTimestamp,
            TestContext.Current.CancellationToken);
        string timestampMutation = await signer.SignAsync(
            message,
            originalPayload,
            FixedTimestamp.AddSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.NotEqual(original, payloadMutation);
        Assert.NotEqual(original, timestampMutation);
    }

    [Fact]
    public async Task TransportSignsExactPayloadAndAddsDefaultHeaders()
    {
        byte[] payload = Encoding.UTF8.GetBytes("{\"id\":123}");
        SigningRecordingHandler handler = new();
        using HttpClient client = CreateClient(handler);
        HmacSha256WebhookRequestSigner signer = new(
            new TestSecretProvider(Encoding.UTF8.GetBytes("whsec_test_secret")));
        WebhookHttpTransport transport = new(
            client,
            classifier: null,
            options: new WebhookHttpTransportOptions
            {
                Signing = new WebhookSigningOptions
                {
                    TimeProvider = new FixedTimeProvider(FixedTimestamp),
                },
            },
            signer: signer);
        WebhookMessage message = CreateMessage(
            payload,
            new Dictionary<string, string>
            {
                ["X-Webhook-Signature"] = "untrusted-value",
            });

        WebhookDeliveryResult result = await transport.SendAsync(
            message,
            TestContext.Current.CancellationToken);

        Assert.Equal(WebhookDeliveryOutcome.Success, result.Outcome);
        Assert.Equal(payload, handler.Body);
        Assert.Equal("webhook-123", handler.GetHeader("X-Webhook-Id"));
        Assert.Equal("order.created", handler.GetHeader("X-Webhook-Event"));
        Assert.Equal("1767323045", handler.GetHeader("X-Webhook-Timestamp"));
        Assert.Equal(
            "v1=3b2bd7a533a3752d16ec9d722ba635dca7a04012c61e46c96892b41efb0a353b",
            handler.GetHeader("X-Webhook-Signature"));
    }

    [Fact]
    public async Task TransportSupportsCustomSigningHeaderNamesAndSigner()
    {
        SigningRecordingHandler handler = new();
        using HttpClient client = CreateClient(handler);
        WebhookHttpTransport transport = new(
            client,
            classifier: null,
            options: new WebhookHttpTransportOptions
            {
                Signing = new WebhookSigningOptions
                {
                    WebhookIdHeaderName = "Webhook-Delivery",
                    EventTypeHeaderName = "Webhook-Type",
                    TimestampHeaderName = "Webhook-Time",
                    SignatureHeaderName = "Webhook-Hmac",
                    TimeProvider = new FixedTimeProvider(FixedTimestamp),
                },
            },
            signer: new ConstantSigner("custom-signature"));

        _ = await transport.SendAsync(
            CreateMessage(new byte[] { 1, 2, 3 }),
            TestContext.Current.CancellationToken);

        Assert.Equal("webhook-123", handler.GetHeader("Webhook-Delivery"));
        Assert.Equal("order.created", handler.GetHeader("Webhook-Type"));
        Assert.Equal("1767323045", handler.GetHeader("Webhook-Time"));
        Assert.Equal("custom-signature", handler.GetHeader("Webhook-Hmac"));
    }

    [Fact]
    public async Task AttemptTimeoutIncludesSigningSecretResolution()
    {
        using HttpClient client = CreateClient(new SigningRecordingHandler());
        WebhookHttpTransport transport = new(
            client,
            classifier: null,
            options: new WebhookHttpTransportOptions
            {
                AttemptTimeout = TimeSpan.FromMilliseconds(25),
            },
            signer: new BlockingSigner());

        WebhookDeliveryResult result = await transport.SendAsync(
            CreateMessage(new byte[] { 1, 2, 3 }),
            TestContext.Current.CancellationToken);

        Assert.Equal(WebhookDeliveryOutcome.RetryableFailure, result.Outcome);
        Assert.Equal(WebhookTransportFailureKind.Timeout, result.FailureKind);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public void TransportPreservesOriginalThreeParameterConstructor()
    {
        System.Reflection.ConstructorInfo? constructor = typeof(WebhookHttpTransport).GetConstructor(
        [
            typeof(HttpClient),
            typeof(IWebhookHttpResponseClassifier),
            typeof(WebhookHttpTransportOptions),
        ]);

        Assert.NotNull(constructor);
    }

    [Fact]
    public async Task SigningFailuresProducedByLibraryDoNotExposeSecret()
    {
        const string secretText = "do-not-expose-this-secret";
        using HttpClient client = CreateClient(new SigningRecordingHandler());
        HmacSha256WebhookRequestSigner signer = new(
            new TestSecretProvider(Encoding.UTF8.GetBytes(secretText)));
        WebhookHttpTransport transport = new(
            client,
            classifier: null,
            options: new WebhookHttpTransportOptions
            {
                Signing = new WebhookSigningOptions
                {
                    SignatureHeaderName = "Content-Type",
                    TimeProvider = new FixedTimeProvider(FixedTimestamp),
                },
            },
            signer: signer);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.SendAsync(
                CreateMessage(new byte[] { 1, 2, 3 }),
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain(secretText, exception.ToString(), StringComparison.Ordinal);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static WebhookMessage CreateMessage(
        ReadOnlyMemory<byte> payload,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        return new WebhookMessage(
            "webhook-123",
            "order.created",
            new Uri("https://example.test/webhooks"),
            payload,
            "application/json",
            headers);
    }

    private sealed class TestSecretProvider : IWebhookSigningSecretProvider
    {
        private readonly byte[] secret;

        public TestSecretProvider(byte[] secret)
        {
            this.secret = secret;
        }

        public ValueTask<ReadOnlyMemory<byte>> GetSecretAsync(
            WebhookMessage message,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(secret);
        }
    }

    private sealed class ConstantSigner : IWebhookRequestSigner
    {
        private readonly string signature;

        public ConstantSigner(string signature)
        {
            this.signature = signature;
        }

        public ValueTask<string> SignAsync(
            WebhookMessage message,
            ReadOnlyMemory<byte> payload,
            DateTimeOffset timestamp,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(signature);
        }
    }

    private sealed class BlockingSigner : IWebhookRequestSigner
    {
        public async ValueTask<string> SignAsync(
            WebhookMessage message,
            ReadOnlyMemory<byte> payload,
            DateTimeOffset timestamp,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return "signature";
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset timestamp;

        public FixedTimeProvider(DateTimeOffset timestamp)
        {
            this.timestamp = timestamp;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return timestamp;
        }
    }

    private sealed class SigningRecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

        public byte[] Body
        {
            get;
            private set;
        } = [];

        public string GetHeader(string name)
        {
            return headers[name];
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                headers[name] = string.Join(",", values);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
