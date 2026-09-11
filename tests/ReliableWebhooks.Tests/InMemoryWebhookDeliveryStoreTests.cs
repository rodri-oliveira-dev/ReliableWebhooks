using ReliableWebhooks;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class InMemoryWebhookDeliveryStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task EnqueueAsyncKeepsOriginalDeliveryWhenIdentifierAlreadyExists()
    {
        InMemoryWebhookDeliveryStore store = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        WebhookMessage original = CreateMessage("webhook-1", 1);
        WebhookMessage duplicate = CreateMessage("webhook-1", 9);

        WebhookEnqueueResult first = await store.EnqueueAsync(original, Now, cancellationToken);
        WebhookEnqueueResult second = await store.EnqueueAsync(
            duplicate,
            Now.AddHours(1),
            cancellationToken);

        Assert.Equal(WebhookEnqueueStatus.Enqueued, first.Status);
        Assert.True(first.WasEnqueued);
        Assert.Equal(WebhookEnqueueStatus.AlreadyExists, second.Status);
        Assert.False(second.WasEnqueued);
        Assert.Same(original, second.Delivery.Message);
        Assert.Equal(Now, second.Delivery.NextAttemptAt);
        Assert.Equal(0, second.Delivery.AttemptCount);
    }

    [Fact]
    public async Task ClaimDueAsyncAllowsOnlyOneConcurrentOwnerForActiveDelivery()
    {
        InMemoryWebhookDeliveryStore store = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);

        TaskCompletionSource<bool> start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<WebhookDeliveryLease>>[] workers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(
                async () =>
                {
                    await start.Task.WaitAsync(cancellationToken);
                    return await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken);
                },
                cancellationToken))
            .ToArray();

        start.SetResult(true);
        IReadOnlyList<WebhookDeliveryLease>[] results = await Task.WhenAll(workers);
        WebhookDeliveryLease[] claimed = results.SelectMany(static leases => leases).ToArray();

        WebhookDeliveryLease lease = Assert.Single(claimed);
        Assert.Equal("webhook-1", lease.Delivery.Message.Id);
        Assert.Equal(1, lease.Delivery.AttemptCount);
        Assert.Equal(DeliveryState.InProgress, lease.Delivery.State);
    }

    [Fact]
    public async Task ExpiredLeaseCanBeReclaimedAndRejectsStaleOwner()
    {
        InMemoryWebhookDeliveryStore store = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);

        WebhookDeliveryLease first = Assert.Single(
            await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken));

        Assert.Empty(
            await store.ClaimDueAsync(Now.AddSeconds(59), LeaseDuration, 1, cancellationToken));

        WebhookDeliveryLease second = Assert.Single(
            await store.ClaimDueAsync(Now.AddMinutes(1), LeaseDuration, 1, cancellationToken));

        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(2, second.Delivery.AttemptCount);

        await Assert.ThrowsAsync<WebhookDeliveryStoreConcurrencyException>(
            () => store.MarkSucceededAsync(first, Now.AddMinutes(1), cancellationToken));
    }

    [Fact]
    public async Task RenewLeaseAsyncExtendsOwnershipWithoutIncrementingAttemptCount()
    {
        InMemoryWebhookDeliveryStore store = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);

        WebhookDeliveryLease lease = Assert.Single(
            await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken));
        WebhookDeliveryLease renewed = await store.RenewLeaseAsync(
            lease,
            Now.AddSeconds(30),
            LeaseDuration,
            cancellationToken);

        Assert.Equal(lease.Token, renewed.Token);
        Assert.Equal(Now.AddSeconds(90), renewed.ExpiresAt);
        Assert.Equal(1, renewed.Delivery.AttemptCount);
        Assert.Empty(
            await store.ClaimDueAsync(Now.AddMinutes(1), LeaseDuration, 1, cancellationToken));
    }

    [Fact]
    public async Task ScheduleRetryAsyncPersistsAttemptCountNextAttemptAndLastError()
    {
        InMemoryWebhookDeliveryStore store = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);
        WebhookDeliveryLease first = Assert.Single(
            await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken));
        DateTimeOffset nextAttemptAt = Now.AddMinutes(5);

        await store.ScheduleRetryAsync(
            first,
            Now.AddSeconds(10),
            nextAttemptAt,
            "timeout",
            cancellationToken);

        WebhookDeliverySnapshot snapshot = Assert.NotNull(
            await store.GetAsync("webhook-1", cancellationToken));
        Assert.Equal(DeliveryState.Failed, snapshot.State);
        Assert.Equal(1, snapshot.AttemptCount);
        Assert.Equal(nextAttemptAt, snapshot.NextAttemptAt);
        Assert.Equal("timeout", snapshot.LastError);
        Assert.Null(snapshot.LeaseExpiresAt);
        Assert.Empty(
            await store.ClaimDueAsync(nextAttemptAt.AddTicks(-1), LeaseDuration, 1, cancellationToken));

        WebhookDeliveryLease second = Assert.Single(
            await store.ClaimDueAsync(nextAttemptAt, LeaseDuration, 1, cancellationToken));
        Assert.Equal(2, second.Delivery.AttemptCount);
    }

    [Fact]
    public async Task MarkSucceededAsyncCreatesTerminalStateAndRejectsFurtherUpdates()
    {
        InMemoryWebhookDeliveryStore store = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);
        WebhookDeliveryLease lease = Assert.Single(
            await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken));

        await store.MarkSucceededAsync(lease, Now.AddSeconds(10), cancellationToken);

        WebhookDeliverySnapshot snapshot = Assert.NotNull(
            await store.GetAsync("webhook-1", cancellationToken));
        Assert.Equal(DeliveryState.Succeeded, snapshot.State);
        Assert.True(snapshot.IsTerminal);
        Assert.Null(snapshot.NextAttemptAt);
        Assert.Null(snapshot.LeaseExpiresAt);
        Assert.Empty(
            await store.ClaimDueAsync(Now.AddDays(1), LeaseDuration, 1, cancellationToken));

        await Assert.ThrowsAsync<WebhookDeliveryStoreConcurrencyException>(
            () => store.DeadLetterAsync(lease, Now.AddSeconds(11), "stale", cancellationToken));
    }

    [Fact]
    public async Task MarkPermanentlyFailedAsyncCreatesTerminalFailure()
    {
        InMemoryWebhookDeliveryStore store = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);
        WebhookDeliveryLease lease = Assert.Single(
            await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken));

        await store.MarkPermanentlyFailedAsync(
            lease,
            Now.AddSeconds(10),
            "HTTP 400",
            cancellationToken);

        WebhookDeliverySnapshot snapshot = Assert.NotNull(
            await store.GetAsync("webhook-1", cancellationToken));
        Assert.Equal(DeliveryState.PermanentlyFailed, snapshot.State);
        Assert.Equal("HTTP 400", snapshot.LastError);
        Assert.True(snapshot.IsTerminal);
        Assert.Empty(
            await store.ClaimDueAsync(Now.AddDays(1), LeaseDuration, 1, cancellationToken));
    }

    [Fact]
    public async Task DeadLetterAsyncCreatesTerminalDeadLetterState()
    {
        InMemoryWebhookDeliveryStore store = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);
        WebhookDeliveryLease lease = Assert.Single(
            await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken));

        await store.DeadLetterAsync(
            lease,
            Now.AddSeconds(10),
            "attempt limit reached",
            cancellationToken);

        WebhookDeliverySnapshot snapshot = Assert.NotNull(
            await store.GetAsync("webhook-1", cancellationToken));
        Assert.Equal(DeliveryState.DeadLettered, snapshot.State);
        Assert.Equal("attempt limit reached", snapshot.LastError);
        Assert.True(snapshot.IsTerminal);
    }

    [Fact]
    public async Task ScheduleRetryAsyncRejectsNextAttemptBeforeCompletion()
    {
        InMemoryWebhookDeliveryStore store = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);
        WebhookDeliveryLease lease = Assert.Single(
            await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ScheduleRetryAsync(
                lease,
                Now.AddSeconds(10),
                Now.AddSeconds(9),
                "error",
                cancellationToken));
    }

    [Fact]
    public async Task OperationsHonorPreCanceledToken()
    {
        InMemoryWebhookDeliveryStore store = new();
        using CancellationTokenSource cancellationTokenSource = new();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.GetAsync("webhook-1", cancellationTokenSource.Token));
    }

    private static WebhookMessage CreateMessage(string id, byte payloadMarker = 1)
    {
        return new WebhookMessage(
            id,
            "order.created",
            new Uri("https://example.test/webhooks"),
            new byte[] { payloadMarker },
            "application/json");
    }
}
