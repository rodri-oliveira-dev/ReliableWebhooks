using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using ReliableWebhooks;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddReliableWebhooksRegistersDefaultsAndPreservesConsumerOverrides()
    {
        ServiceCollection services = new();
        IWebhookDeliveryStore store = Substitute.For<IWebhookDeliveryStore>();
        IWebhookDeliveryTransport transport = Substitute.For<IWebhookDeliveryTransport>();
        IWebhookRetryPolicy retryPolicy = Substitute.For<IWebhookRetryPolicy>();
        IWebhookHttpResponseClassifier classifier = Substitute.For<IWebhookHttpResponseClassifier>();
        IWebhookRequestSigner signer = Substitute.For<IWebhookRequestSigner>();

        services.AddSingleton(store);
        services.AddSingleton(transport);
        services.AddSingleton(retryPolicy);
        services.AddSingleton(classifier);
        services.AddSingleton(signer);

        ReliableWebhooksBuilder builder = services.AddReliableWebhooks();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(store, provider.GetRequiredService<IWebhookDeliveryStore>());
        Assert.Same(transport, provider.GetRequiredService<IWebhookDeliveryTransport>());
        Assert.Same(retryPolicy, provider.GetRequiredService<IWebhookRetryPolicy>());
        Assert.Same(classifier, provider.GetRequiredService<IWebhookHttpResponseClassifier>());
        Assert.Same(signer, provider.GetRequiredService<IWebhookRequestSigner>());
        Assert.NotNull(provider.GetRequiredService<IWebhookEnqueueService>());
        Assert.NotNull(provider.GetRequiredService<WebhookDispatcher>());

        HttpClient client = provider
            .GetRequiredService<IHttpClientFactory>()
            .CreateClient(builder.HttpClientBuilder.Name);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public async Task HostedDispatcherDeliversEnqueuedWebhookThroughConfiguredHttpClient()
    {
        ServiceCollection services = new();
        InMemoryWebhookDeliveryStore store = new();
        BlockingDispatcherDelay delay = new();
        SuccessHandler handler = new();

        services.AddSingleton<IWebhookDeliveryStore>(store);
        services.AddSingleton<IWebhookDispatcherDelay>(delay);

        ReliableWebhooksBuilder builder = services.AddReliableWebhooks(options =>
        {
            options.Dispatcher.MaxConcurrency = 1;
            options.Dispatcher.PollInterval = TimeSpan.FromMinutes(1);
            options.Dispatcher.ShutdownGracePeriod = TimeSpan.FromSeconds(1);
        });
        builder.HttpClientBuilder.ConfigurePrimaryHttpMessageHandler(() => handler);
        _ = builder.AddHostedDispatcher();

        await using ServiceProvider provider = services.BuildServiceProvider();
        IWebhookEnqueueService enqueueService = provider.GetRequiredService<IWebhookEnqueueService>();
        IHostedService hostedService = Assert.Single(provider.GetServices<IHostedService>());
        WebhookMessage message = CreateMessage("hosted-success");

        WebhookEnqueueResult enqueueResult = await enqueueService.EnqueueAsync(
            message,
            TestContext.Current.CancellationToken);
        Assert.Equal(WebhookEnqueueStatus.Enqueued, enqueueResult.Status);

        await hostedService.StartAsync(TestContext.Current.CancellationToken);
        _ = await delay.Entered.WaitAsync(TestContext.Current.CancellationToken);
        await hostedService.StopAsync(TestContext.Current.CancellationToken);

        WebhookDeliverySnapshot? snapshot = await store.GetAsync(
            message.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.Succeeded, snapshot.State);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void InvalidOptionsFailWithActionableValidationMessage()
    {
        ServiceCollection services = new();
        services.AddSingleton<IWebhookDeliveryStore, InMemoryWebhookDeliveryStore>();
        _ = services.AddReliableWebhooks(options => options.Dispatcher.MaxConcurrency = 0);

        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<WebhookDispatcher>());
        Assert.Contains("Dispatcher.MaxConcurrency must be greater than zero.", exception.Failures);
    }

    [Fact]
    public async Task EnqueueServicePropagatesCancellationToStore()
    {
        ServiceCollection services = new();
        services.AddSingleton<IWebhookDeliveryStore, InMemoryWebhookDeliveryStore>();
        _ = services.AddReliableWebhooks();

        using ServiceProvider provider = services.BuildServiceProvider();
        IWebhookEnqueueService enqueueService = provider.GetRequiredService<IWebhookEnqueueService>();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enqueueService.EnqueueAsync(
            CreateMessage("canceled"),
            cancellation.Token));
    }

    private static WebhookMessage CreateMessage(string id)
    {
        return new WebhookMessage(
            id,
            "order.created",
            new Uri("https://example.test/webhooks"),
            "{}"u8.ToArray(),
            "application/json");
    }

    private sealed class BlockingDispatcherDelay : IWebhookDispatcherDelay
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => entered.Task;

        public async Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken = default)
        {
            _ = entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class SuccessHandler : HttpMessageHandler
    {
        internal int RequestCount
        {
            get;
            private set;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
