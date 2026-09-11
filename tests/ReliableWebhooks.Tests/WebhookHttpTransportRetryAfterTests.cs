using System.Net;
using System.Net.Http.Headers;
using ReliableWebhooks;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class WebhookHttpTransportRetryAfterTests
{
    [Fact]
    public async Task SendAsyncCapturesHttpDateRetryAfter()
    {
        DateTimeOffset retryAt = new(2026, 9, 11, 21, 0, 0, TimeSpan.Zero);
        using HttpClient client = CreateClient(new DelegateHandler(
            (_, _) =>
            {
                HttpResponseMessage response = CreateResponse();
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt);
                return Task.FromResult(response);
            }));
        WebhookHttpTransport transport = new(client);

        WebhookDeliveryResult result = await transport.SendAsync(
            CreateMessage(),
            TestContext.Current.CancellationToken);

        Assert.Null(result.RetryAfterDelay);
        Assert.Equal(retryAt, result.RetryAfterDate);
    }

    [Fact]
    public async Task SendAsyncIgnoresMalformedRetryAfter()
    {
        using HttpClient client = CreateClient(new DelegateHandler(
            static (_, _) =>
            {
                HttpResponseMessage response = CreateResponse();
                _ = response.Headers.TryAddWithoutValidation("Retry-After", "not-a-valid-retry-after");
                return Task.FromResult(response);
            }));
        WebhookHttpTransport transport = new(client);

        WebhookDeliveryResult result = await transport.SendAsync(
            CreateMessage(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WebhookDeliveryOutcome.RetryableFailure, result.Outcome);
        Assert.Null(result.RetryAfterDelay);
        Assert.Null(result.RetryAfterDate);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static HttpResponseMessage CreateResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new ByteArrayContent([]),
        };
    }

    private static WebhookMessage CreateMessage()
    {
        return new WebhookMessage(
            "retry-after-test",
            "order.created",
            new Uri("https://example.test/webhooks"),
            new byte[] { 1, 2, 3 },
            "application/json");
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder;

        internal DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            this.responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responder(request, cancellationToken);
        }
    }
}
