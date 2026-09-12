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

        WebhookDeliverySnapshot? snapshot = await store.GetAsync(
            "success",
            TestContext.Current.CancellationToken);
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

        WebhookDeliverySnapshot? snapshot = await store.GetAsync(
            "retry",
            TestContext.Current.CancellationToken);
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

        WebhookDeliverySnapshot? snapshot = await store.GetAsync(
            "permanent",
            TestContext.Current.CancellationToken);
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

        WebhookDeliverySnapshot? snapshot = await store.GetAsync(
            "dead-letter",
            TestContext.Current.CancellationToken);
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
            _ = await store.EnqueueAsync(
                CreateMessage($"concurrency-{index}"),
                Now,
                TestContext.Current.CancellationToken);
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
    public async Task DispatcherRenewsActiveLeaseBeforeOriginalExpiration()
    {
        InMemoryWebhookDeliveryStore store = await CreateStoreAsync("renewed");
        ManualTimeProvider timeProvider = new(Now);
        BlockingDelay delay = new();
        BlockingHandler handler = new(expectedStarts: 1);
        using HttpClient client = CreateClient(handler);
        WebhookHttpTransport transport = new(
            client,
            options: new WebhookHttpTransportOptions
            {
                AttemptTimeout = Timeout.InfiniteTimeSpan,
            });
        using CancellationTokenSource shutdown = new();
        WebhookDispatcher dispatcher = CreateDispatcher(
            store,
            transport,
            delay: delay,
            leaseDuration: TimeSpan.FromSeconds(10),
            timeProvider: timeProvider);

        Task run = dispatcher.RunAsync(shutdown.Token);
        await handler.ExpectedStartsReached.WaitAsync(TestContext.Current.CancellationToken);
        await timeProvider.WaitForTimerCountAsync(1, TestContext.Current.CancellationToken);

        timeProvider.Advance(TimeSpan.FromSeconds(6));

        await WaitUntilAsync(
            async () =>
            {
                WebhookDeliverySnapshot? snapshot = await store.GetAsync(
                    "renewed",
                    TestContext.Current.CancellationToken);
                return snapshot?.LeaseExpiresAt == Now.AddSeconds(16);
            });

        IReadOnlyList<WebhookDeliveryLease> reclaimed = await store.ClaimDueAsync(
            Now.AddSeconds(11),
            TimeSpan.FromSeconds(10),
            1,
            TestContext.Current.CancellationToken);

        Assert.Empty(reclaimed);

        handler.Release();
        await WaitUntilAsync(
            async () =>
            {
                WebhookDeliverySnapshot? snapshot = await store.GetAsync(
                    "renewed",
                    TestContext.Current.CancellationToken);
                return snapshot?.State == DeliveryState.Succeeded;
            });

        shutdown.Cancel();
        await run;
    }

    [Fact]
    public async Task DispatcherCancelsInflightAttemptWhenLeaseRenewalFails()
    {
        InMemoryWebhookDeliveryStore innerStore = await CreateStoreAsync("lease-lost");
        FailingRenewalStore store = new(innerStore);
        BlockingDelay delay = new();
        BlockingTransport transport = new();
        using CancellationTokenSource shutdown = new();
        WebhookDispatcher dispatcher = CreateDispatcher(
            store,
            transport,
            delay: delay,
            leaseDuration: TimeSpan.FromSeconds(1),
            timeProvider: TimeProvider.System);

        Task run = dispatcher.RunAsync(shutdown.Token);
        await transport.Started.WaitAsync(TestContext.Current.CancellationToken);

        await transport.Canceled.WaitAsync(TestContext.Current.CancellationToken);
        shutdown.Cancel();
        await run;

        WebhookDeliverySnapshot? snapshot = await innerStore.GetAsync(
            "lease-lost",
            TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.InProgress, snapshot.State);
        Assert.Equal(1, snapshot.AttemptCount);
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

        WebhookDeliverySnapshot? snapshot = await store.GetAsync(
            "graceful",
            TestContext.Current.CancellationToken);
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

        WebhookDeliverySnapshot? snapshot = await store.GetAsync(
            "cancelled",
            TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.InProgress, snapshot.State);
        Assert.Equal(1, snapshot.AttemptCount);
    }

    [Fact]
    public async Task FaultedAttemptCancelsAndDrainsRemainingWorkBeforeRethrowing()
    {
        InMemoryWebhookDeliveryStore store = new();
        _ = await store.EnqueueAsync(
            CreateMessage("a-fault"),
            Now,
            TestContext.Current.CancellationToken);
        _ = await store.EnqueueAsync(
            CreateMessage("b-blocking"),
            Now,
            TestContext.Current.CancellationToken);
        FaultingAndBlockingTransport transport = new();
        WebhookDispatcher dispatcher = CreateDispatcher(
            store,
            transport,
            delay: new ImmediateDelay(),
            maxConcurrency: 2,
            shutdownGracePeriod: TimeSpan.Zero);

        Task run = dispatcher.RunAsync(TestContext.Current.CancellationToken);
        await transport.BlockingStarted.WaitAsync(TestContext.Current.CancellationToken);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => run.WaitAsync(TestContext.Current.CancellationToken));

        Assert.Equal("transport failure", exception.Message);
        await transport.BlockingCanceled.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(run.IsCompleted);

        WebhookDeliverySnapshot? snapshot = await store.GetAsync(
            "b-blocking",
            TestContext.Current.CancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.InProgress, snapshot.State);
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
        TimeSpan? shutdownGracePeriod = null,
        TimeSpan? leaseDuration = null,
        TimeProvider? timeProvider = null)
    {
        return new WebhookDispatcher(
            store,
            transport,
            retryPolicy ?? new DefaultWebhookRetryPolicy(),
            new WebhookDispatcherOptions
            {
                MaxConcurrency = maxConcurrency,
                LeaseDuration = leaseDuration ?? TimeSpan.FromMinutes(1),
                PollInterval = TimeSpan.FromSeconds(5),
                ShutdownGracePeriod = shutdownGracePeriod ?? TimeSpan.FromSeconds(30),
                TimeProvider = timeProvider ?? new FixedTimeProvider(Now),
            },
            delay);
    }

    private static async Task<InMemoryWebhookDeliveryStore> CreateStoreAsync(string id)
    {
        InMemoryWebhookDeliveryStore store = new();
        _ = await store.EnqueueAsync(
            CreateMessage(id),
            Now,
            TestContext.Current.CancellationToken);
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }

        Assert.Fail("The expected condition was not met.");
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<ManualTimer> timers = [];
        private TaskCompletionSource timersChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DateTimeOffset now;

        public ManualTimeProvider(DateTimeOffset now)
        {
            this.now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate)
            {
                return now;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ManualTimer timer = new(this, callback, state, dueTime, period);
            lock (gate)
            {
                timers.Add(timer);
                timersChanged.TrySetResult();
            }

            return timer;
        }

        public async Task WaitForTimerCountAsync(
            int count,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                Task wait;
                lock (gate)
                {
                    if (timers.Count >= count)
                    {
                        return;
                    }

                    wait = timersChanged.Task;
                }

                await wait.WaitAsync(cancellationToken);

                lock (gate)
                {
                    if (timersChanged.Task.IsCompleted)
                    {
                        timersChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                }
            }
        }

        public void Advance(TimeSpan duration)
        {
            ManualTimer[] dueTimers;

            lock (gate)
            {
                now = now.Add(duration);
                dueTimers = timers.Where(timer => timer.IsDue(now)).ToArray();
            }

            foreach (ManualTimer timer in dueTimers)
            {
                timer.Fire();
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (gate)
            {
                _ = timers.Remove(timer);
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider owner;
            private readonly TimerCallback callback;
            private readonly object? state;
            private readonly object gate = new();
            private DateTimeOffset dueAt;
            private TimeSpan period;
            private bool disposed;

            internal ManualTimer(
                ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                this.owner = owner;
                this.callback = callback;
                this.state = state;
                this.period = period;
                dueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? DateTimeOffset.MaxValue
                    : owner.GetUtcNow().Add(dueTime);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (gate)
                {
                    if (disposed)
                    {
                        return false;
                    }

                    this.period = period;
                    dueAt = dueTime == Timeout.InfiniteTimeSpan
                        ? DateTimeOffset.MaxValue
                        : owner.GetUtcNow().Add(dueTime);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (gate)
                {
                    disposed = true;
                }

                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            internal bool IsDue(DateTimeOffset now)
            {
                lock (gate)
                {
                    return !disposed && dueAt <= now;
                }
            }

            internal void Fire()
            {
                bool shouldFire;

                lock (gate)
                {
                    if (disposed || dueAt == DateTimeOffset.MaxValue)
                    {
                        return;
                    }

                    dueAt = period > TimeSpan.Zero && period != Timeout.InfiniteTimeSpan
                        ? owner.GetUtcNow().Add(period)
                        : DateTimeOffset.MaxValue;
                    shouldFire = true;
                }

                if (shouldFire)
                {
                    callback(state);
                }
            }
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

    private sealed class FaultingAndBlockingTransport : IWebhookDeliveryTransport
    {
        private readonly TaskCompletionSource blockingStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource blockingCanceled = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task BlockingStarted => blockingStarted.Task;

        public Task BlockingCanceled => blockingCanceled.Task;

        public Task<WebhookDeliveryResult> SendAsync(
            WebhookMessage message,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(message.Id, "a-fault", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("transport failure");
            }

            return BlockAsync(cancellationToken);
        }

        private async Task<WebhookDeliveryResult> BlockAsync(CancellationToken cancellationToken)
        {
            blockingStarted.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                blockingCanceled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("The blocking transport completed unexpectedly.");
        }
    }

    private sealed class BlockingTransport : IWebhookDeliveryTransport
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => started.Task;

        public Task Canceled => canceled.Task;

        public void Release()
        {
            release.TrySetResult();
        }

        public async Task<WebhookDeliveryResult> SendAsync(
            WebhookMessage message,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            started.TrySetResult();

            try
            {
                await release.Task.WaitAsync(cancellationToken);
                throw new InvalidOperationException("The blocking transport was released unexpectedly.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                canceled.TrySetResult();
                throw;
            }
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

    private sealed class FailingRenewalStore : IWebhookDeliveryStore
    {
        private readonly IWebhookDeliveryStore innerStore;

        internal FailingRenewalStore(IWebhookDeliveryStore innerStore)
        {
            this.innerStore = innerStore;
        }

        public Task<WebhookEnqueueResult> EnqueueAsync(
            WebhookMessage message,
            DateTimeOffset nextAttemptAt,
            CancellationToken cancellationToken = default)
        {
            return innerStore.EnqueueAsync(message, nextAttemptAt, cancellationToken);
        }

        public Task<IReadOnlyList<WebhookDeliveryLease>> ClaimDueAsync(
            DateTimeOffset now,
            TimeSpan leaseDuration,
            int maxCount,
            CancellationToken cancellationToken = default)
        {
            return innerStore.ClaimDueAsync(now, leaseDuration, maxCount, cancellationToken);
        }

        public Task<WebhookDeliveryLease> RenewLeaseAsync(
            WebhookDeliveryLease lease,
            DateTimeOffset now,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("renewal failed");
        }

        public Task MarkSucceededAsync(
            WebhookDeliveryLease lease,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken = default)
        {
            return innerStore.MarkSucceededAsync(lease, completedAt, cancellationToken);
        }

        public Task ScheduleRetryAsync(
            WebhookDeliveryLease lease,
            DateTimeOffset completedAt,
            DateTimeOffset nextAttemptAt,
            string? lastError,
            CancellationToken cancellationToken = default)
        {
            return innerStore.ScheduleRetryAsync(lease, completedAt, nextAttemptAt, lastError, cancellationToken);
        }

        public Task MarkPermanentlyFailedAsync(
            WebhookDeliveryLease lease,
            DateTimeOffset completedAt,
            string? lastError,
            CancellationToken cancellationToken = default)
        {
            return innerStore.MarkPermanentlyFailedAsync(lease, completedAt, lastError, cancellationToken);
        }

        public Task DeadLetterAsync(
            WebhookDeliveryLease lease,
            DateTimeOffset completedAt,
            string? lastError,
            CancellationToken cancellationToken = default)
        {
            return innerStore.DeadLetterAsync(lease, completedAt, lastError, cancellationToken);
        }

        public Task<WebhookDeliverySnapshot?> GetAsync(
            string webhookId,
            CancellationToken cancellationToken = default)
        {
            return innerStore.GetAsync(webhookId, cancellationToken);
        }
    }
}
