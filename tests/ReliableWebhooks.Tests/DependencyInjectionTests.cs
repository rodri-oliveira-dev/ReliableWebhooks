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
    public async Task DefaultTransportCreatesHttpClientForEachDeliveryAttempt()
    {
        ServiceCollection services = new();
        IHttpClientFactory httpClientFactory = Substitute.For<IHttpClientFactory>();
        SuccessHandler handler = new();
        httpClientFactory
            .CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(handler, disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            });
        services.AddSingleton(httpClientFactory);

        ReliableWebhooksBuilder builder = services.AddReliableWebhooks();

        using ServiceProvider provider = services.BuildServiceProvider();
        IWebhookDeliveryTransport transport = provider.GetRequiredService<IWebhookDeliveryTransport>();

        WebhookDeliveryResult first = await transport.SendAsync(
            CreateMessage("factory-attempt-1"),
            TestContext.Current.CancellationToken);
        WebhookDeliveryResult second = await transport.SendAsync(
            CreateMessage("factory-attempt-2"),
            TestContext.Current.CancellationToken);

        Assert.Equal(WebhookDeliveryOutcome.Success, first.Outcome);
        Assert.Equal(WebhookDeliveryOutcome.Success, second.Outcome);
        Assert.Equal(2, handler.RequestCount);
        httpClientFactory.Received(2).CreateClient(builder.HttpClientBuilder.Name);
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
        await delay.Entered.WaitAsync(TestContext.Current.CancellationToken);
        await hostedService.StopAsync(TestContext.Current.CancellationToken);

        WebhookDeliverySnapshot? snapshot = await store.GetAsync(
            message.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.Succeeded, snapshot.State);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ScopedStoreRegistrationIsResolvedInsideOperationScopes()
    {
        ServiceCollection services = new();
        services.AddSingleton<ScopedStoreState>();
        services.AddScoped<IWebhookDeliveryStore, ScopedTrackingStore>();
        _ = services.AddReliableWebhooks();

        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
        IWebhookEnqueueService enqueueService = provider.GetRequiredService<IWebhookEnqueueService>();
        ScopedStoreState state = provider.GetRequiredService<ScopedStoreState>();

        _ = await enqueueService.EnqueueAsync(
            CreateMessage("scoped-operation-1"),
            TestContext.Current.CancellationToken);
        _ = await enqueueService.EnqueueAsync(
            CreateMessage("scoped-operation-2"),
            TestContext.Current.CancellationToken);

        Assert.True(state.CreatedStoreIds.Length >= 2);
        Assert.Equal(state.CreatedStoreIds.Length, state.DisposedStoreIds.Length);
        Assert.Equal(
            state.CreatedStoreIds.OrderBy(id => id, StringComparer.Ordinal),
            state.DisposedStoreIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task HostedDispatcherSupportsScopedStoreWithValidateScopes()
    {
        ServiceCollection services = new();
        ScopedStoreState state = new();
        BlockingDispatcherDelay delay = new();
        SuccessHandler handler = new();

        services.AddSingleton(state);
        services.AddScoped<IWebhookDeliveryStore, ScopedTrackingStore>();
        services.AddSingleton<IWebhookDispatcherDelay>(delay);

        ReliableWebhooksBuilder builder = services.AddReliableWebhooks(options =>
        {
            options.Dispatcher.MaxConcurrency = 1;
            options.Dispatcher.PollInterval = TimeSpan.FromMinutes(1);
            options.Dispatcher.ShutdownGracePeriod = TimeSpan.FromSeconds(1);
        });
        builder.HttpClientBuilder.ConfigurePrimaryHttpMessageHandler(() => handler);
        _ = builder.AddHostedDispatcher();

        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true,
            });
        IWebhookEnqueueService enqueueService = provider.GetRequiredService<IWebhookEnqueueService>();
        IHostedService hostedService = Assert.Single(provider.GetServices<IHostedService>());
        WebhookMessage message = CreateMessage("hosted-scoped-store");

        _ = await enqueueService.EnqueueAsync(message, TestContext.Current.CancellationToken);

        await hostedService.StartAsync(TestContext.Current.CancellationToken);
        await delay.Entered.WaitAsync(TestContext.Current.CancellationToken);
        await hostedService.StopAsync(TestContext.Current.CancellationToken);

        WebhookDeliverySnapshot? snapshot = await state.InnerStore.GetAsync(
            message.Id,
            TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.Succeeded, snapshot.State);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(state.CreatedStoreIds.Length, state.DisposedStoreIds.Length);
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

    private sealed class ScopedStoreState
    {
        private readonly object gate = new();
        private readonly List<string> createdStoreIds = [];
        private readonly List<string> disposedStoreIds = [];

        internal InMemoryWebhookDeliveryStore InnerStore
        {
            get;
        } = new();

        internal string[] CreatedStoreIds
        {
            get
            {
                lock (gate)
                {
                    return createdStoreIds.ToArray();
                }
            }
        }

        internal string[] DisposedStoreIds
        {
            get
            {
                lock (gate)
                {
                    return disposedStoreIds.ToArray();
                }
            }
        }

        internal void Created(string id)
        {
            lock (gate)
            {
                createdStoreIds.Add(id);
            }
        }

        internal void Disposed(string id)
        {
            lock (gate)
            {
                disposedStoreIds.Add(id);
            }
        }
    }

    private sealed class ScopedTrackingStore : IWebhookDeliveryStore, IDisposable
    {
        private readonly ScopedStoreState state;
        private readonly string id = Guid.NewGuid().ToString("N");

        public ScopedTrackingStore(ScopedStoreState state)
        {
            this.state = state;
            state.Created(id);
        }

        public Task<WebhookEnqueueResult> EnqueueAsync(
            WebhookMessage message,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken = default)
        {
            return state.InnerStore.EnqueueAsync(message, nextAttemptAt, cancellationToken);
        }

        public Task<IReadOnlyList<WebhookDeliveryLease>> ClaimDueAsync(
            DateTimeOffset now,
            TimeSpan leaseDuration,
            int maxCount,
            CancellationToken cancellationToken = default)
        {
            return state.InnerStore.ClaimDueAsync(now, leaseDuration, maxCount, cancellationToken);
        }

        public Task<WebhookDeliveryLease> RenewLeaseAsync(
            WebhookDeliveryLease lease,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            return state.InnerStore.RenewLeaseAsync(lease, now, leaseDuration, cancellationToken);
        }

        public Task MarkSucceededAsync(
            WebhookDeliveryLease lease,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            return state.InnerStore.MarkSucceededAsync(lease, completedAt, cancellationToken);
        }

        public Task ScheduleRetryAsync(
            WebhookDeliveryLease lease,
            DateTimeOffset completedAt,
            DateTimeOffset nextAttemptAt,
            string? lastError,
            CancellationToken cancellationToken = default)
        {
            return state.InnerStore.ScheduleRetryAsync(
                lease,
                completedAt,
                nextAttemptAt,
                lastError,
                cancellationToken);
        }

        public Task MarkPermanentlyFailedAsync(
            WebhookDeliveryLease lease,
            DateTimeOffset completedAt,
            string? lastError,
            CancellationToken cancellationToken = default)
        {
            return state.InnerStore.MarkPermanentlyFailedAsync(
                lease,
                completedAt,
                lastError,
                cancellationToken);
        }

        public Task DeadLetterAsync(
            WebhookDeliveryLease lease,
            DateTimeOffset completedAt,
            string? lastError,
            CancellationToken cancellationToken = default)
        {
            return state.InnerStore.DeadLetterAsync(
                lease,
                completedAt,
                lastError,
                cancellationToken);
        }

        public Task<WebhookDeliverySnapshot?> GetAsync(
            string webhookId,
            CancellationToken cancellationToken = default)
        {
            return state.InnerStore.GetAsync(webhookId, cancellationToken);
        }

        public void Dispose()
        {
            state.Disposed(id);
        }
    }
}
