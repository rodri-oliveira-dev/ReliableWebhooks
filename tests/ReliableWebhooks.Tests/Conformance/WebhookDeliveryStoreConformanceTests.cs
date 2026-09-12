using ReliableWebhooks;
using Xunit;

namespace ReliableWebhooks.Tests.Conformance;

/// <summary>
/// Reusable behavioral contract for <see cref="IWebhookDeliveryStore"/> implementations.
/// </summary>
/// <remarks>
/// Future in-repository persistence adapters should derive from this class and return an isolated store
/// instance from <see cref="CreateStore"/>. Provider-specific tests remain responsible for proving actual
/// durability and for fault-injection scenarios that cannot be expressed through the store abstraction.
/// </remarks>
public abstract class WebhookDeliveryStoreConformanceTests
{
    protected static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
    protected static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);

    protected abstract IWebhookDeliveryStore CreateStore();

    [Fact]
    public async Task EnqueueIsIdempotentAndPreservesOriginalDelivery()
    {
        IWebhookDeliveryStore store = CreateStore();
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
        Assert.Equal(original.Id, second.Delivery.Message.Id);
        Assert.Equal(original.EventType, second.Delivery.Message.EventType);
        Assert.Equal(original.Destination, second.Delivery.Message.Destination);
        Assert.Equal(original.ContentType, second.Delivery.Message.ContentType);
        Assert.Equal(original.Payload.ToArray(), second.Delivery.Message.Payload.ToArray());
        Assert.Equal(original.Headers, second.Delivery.Message.Headers);
        Assert.Equal(Now, second.Delivery.NextAttemptAt);
        Assert.Equal(0, second.Delivery.AttemptCount);
    }

    [Fact]
    public async Task StableIdentifiersAreOpaqueAndCaseSensitive()
    {
        IWebhookDeliveryStore store = CreateStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        WebhookEnqueueResult lower = await store.EnqueueAsync(
            CreateMessage("webhook-1"),
            Now,
            cancellationToken);
        WebhookEnqueueResult upper = await store.EnqueueAsync(
            CreateMessage("WEBHOOK-1"),
            Now,
            cancellationToken);

        Assert.Equal(WebhookEnqueueStatus.Enqueued, lower.Status);
        Assert.Equal(WebhookEnqueueStatus.Enqueued, upper.Status);
        Assert.Equal("webhook-1", lower.Delivery.Message.Id);
        Assert.Equal("WEBHOOK-1", upper.Delivery.Message.Id);
    }

    [Fact]
    public async Task ClaimDueAllowsOnlyOneConcurrentOwnerForActiveDelivery()
    {
        IWebhookDeliveryStore store = CreateStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);

        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IReadOnlyList<WebhookDeliveryLease>>[] workers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(
                async () =>
                {
                    await start.Task.WaitAsync(cancellationToken);
                    return await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken);
                },
                cancellationToken))
            .ToArray();

        start.SetResult();
        IReadOnlyList<WebhookDeliveryLease>[] results = await Task.WhenAll(workers);
        WebhookDeliveryLease lease = Assert.Single(results.SelectMany(static leases => leases));

        Assert.Equal("webhook-1", lease.Delivery.Message.Id);
        Assert.Equal(1, lease.Delivery.AttemptCount);
        Assert.Equal(DeliveryState.InProgress, lease.Delivery.State);
        Assert.NotEqual(Guid.Empty, lease.Token);
        Assert.Equal(Now.Add(LeaseDuration), lease.ExpiresAt);
    }

    [Fact]
    public async Task ClaimDueReturnsOnlyDueWorkAndHonorsMaximumCount()
    {
        IWebhookDeliveryStore store = CreateStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("due-1"), Now, cancellationToken);
        await store.EnqueueAsync(CreateMessage("due-2"), Now, cancellationToken);
        await store.EnqueueAsync(CreateMessage("future"), Now.AddMinutes(5), cancellationToken);

        IReadOnlyList<WebhookDeliveryLease> first = await store.ClaimDueAsync(
            Now,
            LeaseDuration,
            1,
            cancellationToken);
        IReadOnlyList<WebhookDeliveryLease> second = await store.ClaimDueAsync(
            Now,
            LeaseDuration,
            5,
            cancellationToken);

        Assert.Single(first);
        Assert.Single(second);
        Assert.DoesNotContain(first.Concat(second), lease => lease.Delivery.Message.Id == "future");
    }

    [Fact]
    public async Task ActiveLeaseRejectsMismatchedToken()
    {
        IWebhookDeliveryStore store = CreateStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);

        WebhookDeliveryLease lease = Assert.Single(
            await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken));
        WebhookDeliveryLease mismatched = new(
            lease.Delivery,
            Guid.NewGuid(),
            lease.ExpiresAt);

        await Assert.ThrowsAsync<WebhookDeliveryStoreConcurrencyException>(
            () => store.MarkSucceededAsync(mismatched, Now.AddSeconds(1), cancellationToken));

        WebhookDeliverySnapshot? snapshot = await store.GetAsync("webhook-1", cancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.InProgress, snapshot.State);
        Assert.Equal(lease.ExpiresAt, snapshot.LeaseExpiresAt);
    }

    [Fact]
    public async Task ExpiredLeaseCanBeReclaimedAndRejectsStaleOwner()
    {
        IWebhookDeliveryStore store = CreateStore();
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
    public async Task RenewLeaseExtendsOwnershipWithoutIncrementingAttemptCount()
    {
        IWebhookDeliveryStore store = CreateStore();
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
    public async Task RenewLeaseDoesNotShortenExistingExpiration()
    {
        IWebhookDeliveryStore store = CreateStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);

        WebhookDeliveryLease lease = Assert.Single(
            await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken));
        WebhookDeliveryLease renewed = await store.RenewLeaseAsync(
            lease,
            Now.AddSeconds(10),
            TimeSpan.FromSeconds(20),
            cancellationToken);

        Assert.Equal(lease.Token, renewed.Token);
        Assert.Equal(lease.ExpiresAt, renewed.ExpiresAt);
        Assert.Equal(lease.ExpiresAt, renewed.Delivery.LeaseExpiresAt);
        Assert.Equal(1, renewed.Delivery.AttemptCount);
        Assert.Empty(
            await store.ClaimDueAsync(Now.AddSeconds(59), LeaseDuration, 1, cancellationToken));
    }

    [Fact]
    public async Task RetryPersistsSchedulingStateAndCanBeClaimedAgain()
    {
        IWebhookDeliveryStore store = CreateStore();
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

        WebhookDeliverySnapshot? snapshot = await store.GetAsync("webhook-1", cancellationToken);
        Assert.NotNull(snapshot);
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
    public async Task SuccessIsTerminalAndRejectsFurtherLeaseUpdates()
    {
        IWebhookDeliveryStore store = CreateStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);
        WebhookDeliveryLease lease = Assert.Single(
            await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken));

        await store.MarkSucceededAsync(lease, Now.AddSeconds(10), cancellationToken);

        WebhookDeliverySnapshot? snapshot = await store.GetAsync("webhook-1", cancellationToken);
        Assert.NotNull(snapshot);
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
    public async Task PermanentFailureIsTerminal()
    {
        IWebhookDeliveryStore store = CreateStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);
        WebhookDeliveryLease lease = Assert.Single(
            await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken));

        await store.MarkPermanentlyFailedAsync(
            lease,
            Now.AddSeconds(10),
            "HTTP 400",
            cancellationToken);

        WebhookDeliverySnapshot? snapshot = await store.GetAsync("webhook-1", cancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.PermanentlyFailed, snapshot.State);
        Assert.Equal("HTTP 400", snapshot.LastError);
        Assert.True(snapshot.IsTerminal);
        Assert.Null(snapshot.NextAttemptAt);
        Assert.Null(snapshot.LeaseExpiresAt);
        Assert.Empty(
            await store.ClaimDueAsync(Now.AddDays(1), LeaseDuration, 1, cancellationToken));
    }

    [Fact]
    public async Task DeadLetterIsTerminal()
    {
        IWebhookDeliveryStore store = CreateStore();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await store.EnqueueAsync(CreateMessage("webhook-1"), Now, cancellationToken);
        WebhookDeliveryLease lease = Assert.Single(
            await store.ClaimDueAsync(Now, LeaseDuration, 1, cancellationToken));

        await store.DeadLetterAsync(
            lease,
            Now.AddSeconds(10),
            "attempt limit reached",
            cancellationToken);

        WebhookDeliverySnapshot? snapshot = await store.GetAsync("webhook-1", cancellationToken);
        Assert.NotNull(snapshot);
        Assert.Equal(DeliveryState.DeadLettered, snapshot.State);
        Assert.Equal("attempt limit reached", snapshot.LastError);
        Assert.True(snapshot.IsTerminal);
        Assert.Null(snapshot.NextAttemptAt);
        Assert.Null(snapshot.LeaseExpiresAt);
        Assert.Empty(
            await store.ClaimDueAsync(Now.AddDays(1), LeaseDuration, 1, cancellationToken));
    }

    [Fact]
    public async Task PreCanceledTokensAreObservedByAllStoreOperations()
    {
        IWebhookDeliveryStore store = CreateStore();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        CancellationToken token = cancellation.Token;
        WebhookMessage message = CreateMessage("webhook-1");
        WebhookDeliverySnapshot snapshot = new(
            message,
            DeliveryState.InProgress,
            1,
            nextAttemptAt: null,
            lastError: null,
            leaseExpiresAt: Now.Add(LeaseDuration));
        WebhookDeliveryLease lease = new(snapshot, Guid.NewGuid(), Now.Add(LeaseDuration));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.EnqueueAsync(message, Now, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ClaimDueAsync(Now, LeaseDuration, 1, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.RenewLeaseAsync(lease, Now, LeaseDuration, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.MarkSucceededAsync(lease, Now, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ScheduleRetryAsync(lease, Now, Now.AddMinutes(1), "error", token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.MarkPermanentlyFailedAsync(lease, Now, "error", token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.DeadLetterAsync(lease, Now, "error", token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.GetAsync(message.Id, token));
    }

    protected static WebhookMessage CreateMessage(string id, byte payloadMarker = 1)
    {
        return new WebhookMessage(
            id,
            "order.created",
            new Uri("https://example.test/webhooks"),
            new byte[] { payloadMarker },
            "application/json",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["X-Test"] = "value",
            });
    }
}
