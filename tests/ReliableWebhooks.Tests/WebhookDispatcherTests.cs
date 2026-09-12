using System.Net;
using ReliableWebhooks;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class WebhookDispatcherTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DispatcherMarksSuccessfulDeliveryAsSucceeded()
    {
        InMemoryWebhookDeliveryStore store = await CreateStoreAsync("success");
        using HttpClient client = CreateClient(new StatusHandler(HttpStatusCode.NoContent));
        WebhookHttpTransport transport = new(client);
        BlockingDelay delay = new();
        using CancellationTokenSource shutdown = new();
        WebhookDispatcher dispatcher = CreateDispatcher(store, transport, delay: delay);

        Task run = dispatcher.RunAsync(shutdown.Token);
        _ = await delay.Entered.WaitAsync(TestContext.Current.CancellationToken);
        shutdown.Cancel();
        await run;

        WebhookDeliverySnapshot? snapshot = await store.GetAsync("success");
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.Succeeded, snapshot.State);
        Assert.Equal(1, snapshot.AttemptCount);
    }

    [Fact]
    public async Task DispatcherSchedulesRetryableFailure()
    {
        InMemoryWebhookDeliveryStore store = await CreateStoreAsync("retry");
        using HttpClient client = CreateClient(new StatusHandler(HttpStatusCode.ServiceUnavailable));
        WebhookHttpTransport transport = new(client);
        BlockingDelay delay = new();
        using CancellationTokenSource shutdown = new();
        DefaultWebhookRetryPolicy retryPolicy = new(
            new WebhookRetryPolicyOptions
            {
                MaxAttempts = 3,
                BaseDelay = TimeSpan.FromMinutes(1),
                MaxDelay = TimeSpan.FromMinutes(1),
                JitterFactor = 0,
            });
        WebhookDispatcher dispatcher = CreateDispatcher(store, transport, retryPolicy, delay);

        Task run = dispatcher.RunAsync(shutdown.Token);
        _ = await delay.Entered.WaitAsync(TestContext.Current.CancellationToken);
        shutdown.Cancel();
        await run;

        WebhookDeliverySnapshot? snapshot = await store.GetAsync("retry");
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.Failed, snapshot.State);
        Assert.Equal(Now.AddMinutes(1), snapshot.NextAttemptAt);
        Assert.Equal("HTTP 503", snapshot.LastError);
    }

    [Fact]
    public async Task DispatcherMarksPermanentFailure()
    {
        InMemoryWebhookDeliveryStore store = await CreateStoreAsync("permanent");
        using HttpClient client = CreateClient(new StatusHandler(HttpStatusCode.BadRequest));
        WebhookHttpTransport transport = new(client);
        BlockingDelay delay = new();
        using CancellationTokenSource shutdown = new();
        WebhookDispatcher dispatcher = CreateDispatcher(store, transport, delay: delay);

        Task run = dispatcher.RunAsync(shutdown.Token);
        _ = await delay.Entered.WaitAsync(TestContext.Current.CancellationToken);
        shutdown.Cancel();
        await run;

        WebhookDeliverySnapshot? snapshot = await store.GetAsync("permanent");
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.PermanentlyFailed, snapshot.State);
        Assert.Equal("HTTP 400", snapshot.LastError);
    }

    [Fact]
    public async Task DispatcherDeadLettersWhenRetryPolicyExhaustsAttempts()
    {
        InMemoryWebhookDeliveryStore store = await CreateStoreAsync("dead-letter");
        using HttpClient client = CreateClient(new StatusHandler(HttpStatusCode.ServiceUnavailable));
        WebhookHttpTransport transport = new(client);
        BlockingDelay delay = new();
        using CancellationTokenSource shutdown = new();
        DefaultWebhookRetryPolicy retryPolicy = new(
            new WebhookRetryPolicyOptions
            {
                MaxAttempts = 1,
                BaseDelay = TimeSpan.FromSeconds(1),
                MaxDelay = TimeSpan.FromSeconds(1),
                JitterFactor = 0,
            });
        WebhookDispatcher dispatcher = CreateDispatcher(store, transport, retryPolicy, delay);

        Task run = dispatcher.RunAsync(shutdown.Token);
        _ = await delay.Entered.WaitAsync(TestContext.Current.CancellationToken);
        shutdown.Cancel();
        await run;

        WebhookDeliverySnapshot? snapshot = await store.GetAsync("dead-letter");
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.DeadLettered, snapshot.State);
        Assert.Equal("HTTP 503", snapshot.LastError);
    }

    [Fact]
    public async Task DispatcherEnforcesMaximumConcurrencyAndStopsClaimingOnShutdown()
    {
        InMemoryWebhookDeliveryStore store = new();
        for (int index = 0; index < 4; index++)
        {
            _ = await store.EnqueueAsync(CreateMessage($"concurrency-{index}"), Now);
        }

        BlockingHandler handler = new(expectedStarts: 2);
        using HttpClient client = CreateClient(handler);
        WebhookHttpTransport transport = new(client);
        BlockingDelay delay = new();
        using CancellationTokenSource shutdown = new();
        WebhookDispatcher dispatcher = CreateDispatcher(
            store,
            transport,
            delay: delay,
            maxConcurrency: 2,
            shutdownGracePeriod: TimeSpan.FromMinutes(1));

        Task run = dispatcher.RunAsync(shutdown.Token);
        await handler.ExpectedStartsReached.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(2, handler.MaxConcurrentCalls);

        shutdown.Cancel();
        handler.Release();
        await run;

        Assert.Equal(2, handler.CallCount);
        Assert.Equal(2, handler.MaxConcurrentCalls);
    }

    [Fact]
    public async Task ConcurrentDispatchersDoNotProcessSameActiveLease()
    {
        InMemoryWebhookDeliveryStore store = await CreateStoreAsync("shared");
        BlockingHandler handler = new(expectedStarts: 1);
        using HttpClient client = CreateClient(handler);
        WebhookHttpTransport transport = new(client);
        BlockingDelay firstDelay = new();
        BlockingDelay secondDelay = new();
        using CancellationTokenSource firstShutdown = new();
        using CancellationTokenSource secondShutdown = new();
        WebhookDispatcher first = CreateDispatcher(store, transport, delay: firstDelay, maxConcurrency: 1);
        WebhookDispatcher second = CreateDispatcher(store, transport, delay: secondDelay, maxConcurrency: 1);

        Task firstRun = first.RunAsync(firstShutdown.Token);
        await handler.ExpectedStartsReached.WaitAsync(TestContext.Current.CancellationToken);
        Task secondRun = second.RunAsync(secondShutdown.Token);
        _ = await secondDelay.Entered.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, handler.CallCount);

        firstShutdown.Cancel();
        secondShutdown.Cancel();
        handler.Release();
        await Task.WhenAll(firstRun, secondRun);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ShutdownAllowsInflightDeliveryToFinishWithinGracePeriod()
    {
        InMemoryWebhookDeliveryStore store = await CreateStoreAsync("graceful");
        BlockingHandler handler = new(expectedStarts: 1);
        using HttpClient client = CreateClient(handler);
        WebhookHttpTransport transport = new(client);
        BlockingDelay delay = new();
        using CancellationTokenSource shutdown = new();
        WebhookDispatcher dispatcher = CreateDispatcher(
            store,
            transport,
            delay: delay,
            shutdownGracePeriod: TimeSpan.FromMinutes(1));

        Task run = dispatcher.RunAsync(shutdown.Token);
        await handler.ExpectedStartsReached.WaitAsync(TestContext.Current.CancellationToken);

        shutdown.Cancel();
        _ = await delay.Entered.WaitAsync(TestContext.Current.CancellationToken);
        handler.Release();
        await run;

        WebhookDeliverySnapshot? snapshot = await store.GetAsync("graceful");
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.Succeeded, snapshot.State);
    }

    [Fact]
    public async Task GracePeriodExpirationCancelsInflightAttemptWithoutMarkingSuccess()
    {
        InMemoryWebhookDeliveryStore store = await CreateStoreAsync("cancelled");
        BlockingHandler handler = new(expectedStarts: 1);
        using HttpClient client = CreateClient(handler);
        WebhookHttpTransport transport = new(client);
        ImmediateDelay delay = new();
        using CancellationTokenSource shutdown = new();
        WebhookDispatcher dispatcher = CreateDispatcher(
            store,
            transport,
            delay: delay,
            shutdownGracePeriod: TimeSpan.Zero);

        Task run = dispatcher.RunAsync(shutdown.Token);
        await handler.ExpectedStartsReached.WaitAsync(TestContext.Current.CancellationToken);

        shutdown.Cancel();
        await run;

        WebhookDeliverySnapshot? snapshot = await store.GetAsync("cancelled");
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.InProgress, snapshot.State);
        Assert.Equal(1, snapshot.AttemptCount);
    }

    [Fact]
    public async Task EmptyDispatcherUsesControllablePollingDelay()
    {
        InMemoryWebhookDeliveryStore store = new();
        BlockingDelay delay = new();
        using HttpClient client = CreateClient(new StatusHandler(HttpStatusCode.NoContent));
        WebhookDispatcher dispatcher = CreateDispatcher(store, new WebhookHttpTransport(client), delay: delay);
        using CancellationTokenSource shutdown = new();

        Task run = dispatcher.RunAsync(shutdown.Token);
        TimeSpan observedDelay = await delay.Entered.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(5), observedDelay);

        shutdown.Cancel();
        await run;
    }

    private static WebhookDispatcher CreateDispatcher(
        IWebhookDeliveryStore store,
        IWebhookDeliveryTransport transport,
        IWebhookRetryPolicy? retryPolicy = null,
        IWebhookDispatcherDelay? delay = null,
        int maxConcurrency = 1,
        TimeSpan? shutdownGracePeriod = null)
    {
        return new WebhookDispatcher(
            store,
            transport,
            retryPolicy ?? new DefaultWebhookRetryPolicy(),
            new WebhookDispatcherOptions
            {
                MaxConcurrency = maxConcurrency,
                LeaseDuration = TimeSpan.FromMinutes(1),
                PollInterval = TimeSpan.FromSeconds(5),
                ShutdownGracePeriod = shutdownGracePeriod ?? TimeSpan.FromSeconds(30),
                TimeProvider = new FixedTimeProvider(Now),
            },
            delay);
    }

    private static async Task<InMemoryWebhookDeliveryStore> CreateStoreAsync(string id)
    {
        InMemoryWebhookDeliveryStore store = new();
        _ = await store.EnqueueAsync(CreateMessage(id), Now);
        return store;
    }

    private static WebhookMessage CreateMessage(string id)
    {
        return new WebhookMessage(
            id,
            "order.created",
            new Uri("https://example.test/webhooks"),
            new byte[] { 1, 2, 3 },
            "application/json");
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            this.now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return now;
        }
    }

    private sealed class BlockingDelay : IWebhookDispatcherDelay
    {
        private readonly TaskCompletionSource<TimeSpan> entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TimeSpan> Entered => entered.Task;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult(delay);
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ImmediateDelay : IWebhookDispatcherDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode statusCode;

        public StatusHandler(HttpStatusCode statusCode)
        {
            this.statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly int expectedStarts;
        private readonly TaskCompletionSource expectedStartsReached = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int activeCalls;
        private int callCount;
        private int maxConcurrentCalls;

        public BlockingHandler(int expectedStarts)
        {
            this.expectedStarts = expectedStarts;
        }

        public int CallCount => Volatile.Read(ref callCount);

        public int MaxConcurrentCalls => Volatile.Read(ref maxConcurrentCalls);

        public Task ExpectedStartsReached => expectedStartsReached.Task;

        public void Release()
        {
            release.TrySetResult();
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int calls = Interlocked.Increment(ref callCount);
            int active = Interlocked.Increment(ref activeCalls);
            UpdateMaximum(active);

            if (calls >= expectedStarts)
            {
                expectedStartsReached.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            finally
            {
                _ = Interlocked.Decrement(ref activeCalls);
            }
        }

        private void UpdateMaximum(int active)
        {
            int observed = Volatile.Read(ref maxConcurrentCalls);
            while (active > observed)
            {
                int previous = Interlocked.CompareExchange(ref maxConcurrentCalls, active, observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }
}
