using System.Net;
using System.Net.Http.Headers;
using ReliableWebhooks;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class WebhookHttpTransportTests
{
    [Fact]
    public async Task SendAsyncSendsPayloadContentTypeAndCustomHeadersExactlyOnce()
    {
        RecordingHandler handler = new(
            static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using HttpClient client = CreateClient(handler);
        WebhookHttpTransport transport = new(client);
        WebhookMessage message = CreateMessage(
            contentType: "application/vnd.reliable+json",
            headers: new Dictionary<string, string>
            {
                ["X-Webhook-Signature"] = "signature-value",
            });

        WebhookDeliveryResult result = await transport.SendAsync(
            message,
            TestContext.Current.CancellationToken);

        Assert.Equal(WebhookDeliveryOutcome.Success, result.Outcome);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(new Uri("https://example.test/webhooks"), handler.RequestUri);
        Assert.Equal(new byte[] { 1, 2, 3 }, handler.Body);
        Assert.Equal("application/vnd.reliable+json", handler.ContentType);
        Assert.Equal("signature-value", handler.Signature);
    }

    [Theory]
    [InlineData(200, WebhookDeliveryOutcome.Success)]
    [InlineData(204, WebhookDeliveryOutcome.Success)]
    [InlineData(301, WebhookDeliveryOutcome.PermanentFailure)]
    [InlineData(302, WebhookDeliveryOutcome.PermanentFailure)]
    [InlineData(303, WebhookDeliveryOutcome.PermanentFailure)]
    [InlineData(307, WebhookDeliveryOutcome.PermanentFailure)]
    [InlineData(308, WebhookDeliveryOutcome.PermanentFailure)]
    [InlineData(408, WebhookDeliveryOutcome.RetryableFailure)]
    [InlineData(425, WebhookDeliveryOutcome.RetryableFailure)]
    [InlineData(429, WebhookDeliveryOutcome.RetryableFailure)]
    [InlineData(500, WebhookDeliveryOutcome.RetryableFailure)]
    [InlineData(503, WebhookDeliveryOutcome.RetryableFailure)]
    [InlineData(400, WebhookDeliveryOutcome.PermanentFailure)]
    [InlineData(401, WebhookDeliveryOutcome.PermanentFailure)]
    [InlineData(404, WebhookDeliveryOutcome.PermanentFailure)]
    public async Task SendAsyncUsesDefaultStatusClassification(int statusCode, WebhookDeliveryOutcome expected)
    {
        using HttpClient client = CreateClient(new DelegateHandler(
            (_, _) => Task.FromResult(CreateResponse(statusCode))));
        WebhookHttpTransport transport = new(client);

        WebhookDeliveryResult result = await transport.SendAsync(
            CreateMessage(),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(statusCode, result.StatusCode);
        Assert.Equal(WebhookTransportFailureKind.None, result.FailureKind);
    }

    [Fact]
    public async Task SendAsyncAllowsConsumerClassifierToOverrideDefaults()
    {
        using HttpClient client = CreateClient(new DelegateHandler(
            static (_, _) => Task.FromResult(CreateResponse(409))));
        WebhookHttpTransport transport = new(client, new ConflictRetryableClassifier());

        WebhookDeliveryResult result = await transport.SendAsync(
            CreateMessage(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WebhookDeliveryOutcome.RetryableFailure, result.Outcome);
        Assert.Equal(409, result.StatusCode);
    }

    [Fact]
    public async Task SendAsyncClassifiesNetworkFailureAsRetryable()
    {
        using HttpClient client = CreateClient(new DelegateHandler(
            static (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"))));
        WebhookHttpTransport transport = new(client);

        WebhookDeliveryResult result = await transport.SendAsync(
            CreateMessage(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WebhookDeliveryOutcome.RetryableFailure, result.Outcome);
        Assert.Null(result.StatusCode);
        Assert.Equal(WebhookTransportFailureKind.Network, result.FailureKind);
    }

    [Fact]
    public async Task SendAsyncReturnsTimeoutResultWhenAttemptTimeoutExpires()
    {
        DelegateHandler handler = new(
            static async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        using HttpClient client = CreateClient(handler);
        WebhookHttpTransport transport = new(
            client,
            options: new WebhookHttpTransportOptions
            {
                AttemptTimeout = TimeSpan.FromMilliseconds(25),
            });

        WebhookDeliveryResult result = await transport.SendAsync(
            CreateMessage(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WebhookDeliveryOutcome.RetryableFailure, result.Outcome);
        Assert.Equal(WebhookTransportFailureKind.Timeout, result.FailureKind);
        Assert.Null(result.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SendAsyncPropagatesCallerCancellation()
    {
        DelegateHandler handler = new(
            static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using HttpClient client = CreateClient(handler);
        WebhookHttpTransport transport = new(client);
        using CancellationTokenSource source = new();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.SendAsync(CreateMessage(), source.Token));

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SendAsyncReturnsPermanentFailureWithoutSendingWhenDestinationPolicyDenies()
    {
        DelegateHandler handler = new(
            static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));
        using HttpClient client = CreateClient(handler);
        WebhookHttpTransport transport = new(
            client,
            options: new WebhookHttpTransportOptions
            {
                DestinationPolicy = new DenyAllDestinationPolicy(),
            });

        WebhookDeliveryResult result = await transport.SendAsync(
            CreateMessage(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WebhookDeliveryOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(WebhookTransportFailureKind.DestinationPolicyDenied, result.FailureKind);
        Assert.Null(result.StatusCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task SendAsyncBoundsCapturedResponseBody()
    {
        byte[] responseBody = [1, 2, 3, 4, 5, 6];
        using HttpClient client = CreateClient(new DelegateHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBody),
            })));
        WebhookHttpTransport transport = new(
            client,
            options: new WebhookHttpTransportOptions
            {
                MaxResponseBodyBytes = 4,
            });

        WebhookDeliveryResult result = await transport.SendAsync(
            CreateMessage(),
            TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.ResponseBody.ToArray());
        Assert.True(result.ResponseBodyTruncated);
    }

    [Fact]
    public async Task SendAsyncCapturesRetryAfterMetadata()
    {
        using HttpClient client = CreateClient(new DelegateHandler(
            static (_, _) =>
            {
                HttpResponseMessage response = CreateResponse(429);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
                return Task.FromResult(response);
            }));
        WebhookHttpTransport transport = new(client);

        WebhookDeliveryResult result = await transport.SendAsync(
            CreateMessage(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WebhookDeliveryOutcome.RetryableFailure, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(45), result.RetryAfterDelay);
        Assert.Null(result.RetryAfterDate);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static WebhookMessage CreateMessage(
        string contentType = "application/json",
        IReadOnlyDictionary<string, string>? headers = null)
    {
        return new WebhookMessage(
            "webhook-123",
            "order.created",
            new Uri("https://example.test/webhooks"),
            new byte[] { 1, 2, 3 },
            contentType,
            headers);
    }

    private static HttpResponseMessage CreateResponse(int statusCode)
    {
        return new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            Content = new ByteArrayContent([]),
        };
    }

    private sealed class ConflictRetryableClassifier : IWebhookHttpResponseClassifier
    {
        public WebhookDeliveryOutcome Classify(int statusCode)
        {
            return statusCode == 409
                ? WebhookDeliveryOutcome.RetryableFailure
                : new DefaultWebhookHttpResponseClassifier().Classify(statusCode);
        }
    }

    private sealed class DenyAllDestinationPolicy : IWebhookDestinationPolicy
    {
        public ValueTask<WebhookDestinationPolicyResult> AuthorizeAsync(
            Uri destination,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(WebhookDestinationPolicyResult.Deny());
        }
    }

    private class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        public int CallCount
        {
            get;
            protected set;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return responder(request, cancellationToken);
        }
    }

    private sealed class RecordingHandler : DelegateHandler
    {
        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
            : base(responder)
        {
        }

        public HttpMethod? Method
        {
            get;
            private set;
        }

        public Uri? RequestUri
        {
            get;
            private set;
        }

        public byte[]? Body
        {
            get;
            private set;
        }

        public string? ContentType
        {
            get;
            private set;
        }

        public string? Signature
        {
            get;
            private set;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            ContentType = request.Content?.Headers.ContentType?.ToString();
            Signature = request.Headers.GetValues("X-Webhook-Signature").Single();

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
