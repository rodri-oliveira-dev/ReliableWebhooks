using ReliableWebhooks;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class DeliveryModelTests
{
    [Fact]
    public void DeliveryStateContainsTheExpectedLifecycleStates()
    {
        DeliveryState[] expected =
        [
            DeliveryState.Pending,
            DeliveryState.InProgress,
            DeliveryState.Succeeded,
            DeliveryState.Failed,
            DeliveryState.DeadLettered,
            DeliveryState.PermanentlyFailed,
        ];

        Assert.Equal(expected, Enum.GetValues<DeliveryState>());
    }

    [Fact]
    public void DeliveryAttemptRejectsNonPositiveAttemptNumbers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeliveryAttempt(0, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DeliveryAttemptRejectsCompletionBeforeStart()
    {
        DateTimeOffset startedAt = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DeliveryAttempt(1, startedAt, startedAt.AddSeconds(-1)));
    }

    [Fact]
    public void WebhookDeliveryCapturesAttemptSnapshot()
    {
        WebhookMessage message = CreateMessage();
        List<DeliveryAttempt> attempts =
        [
            new DeliveryAttempt(
                1,
                new DateTimeOffset(2026, 9, 11, 12, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 9, 11, 12, 0, 1, TimeSpan.Zero),
                "timeout"),
        ];

        WebhookDelivery delivery = new(message, DeliveryState.Failed, attempts);
        attempts.Clear();

        Assert.Same(message, delivery.Message);
        Assert.Equal(DeliveryState.Failed, delivery.State);
        Assert.Single(delivery.Attempts);
        Assert.Equal(1, delivery.Attempts[0].Number);
        Assert.Equal("timeout", delivery.Attempts[0].Error);
    }

    [Fact]
    public void WebhookDeliveryRejectsUndefinedState()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WebhookDelivery(CreateMessage(), (DeliveryState)999));
    }

    [Fact]
    public void StoreResultModelsCanBeConstructedByConsumers()
    {
        WebhookMessage message = CreateMessage();
        DateTimeOffset expiresAt = new(2026, 9, 11, 12, 5, 0, TimeSpan.Zero);
        Guid token = Guid.NewGuid();

        WebhookDeliverySnapshot snapshot = new(
            message,
            DeliveryState.InProgress,
            2,
            nextAttemptAt: null,
            lastError: "timeout",
            leaseExpiresAt: expiresAt);
        WebhookDeliveryLease lease = new(snapshot, token, expiresAt);
        WebhookEnqueueResult enqueueResult = new(WebhookEnqueueStatus.AlreadyExists, snapshot);

        Assert.Same(message, snapshot.Message);
        Assert.Equal(2, snapshot.AttemptCount);
        Assert.Same(snapshot, lease.Delivery);
        Assert.Equal(token, lease.Token);
        Assert.Equal(expiresAt, lease.ExpiresAt);
        Assert.Same(snapshot, enqueueResult.Delivery);
        Assert.Equal(WebhookEnqueueStatus.AlreadyExists, enqueueResult.Status);
    }

    [Fact]
    public void StoreResultModelsRejectInvalidPublicConstructionArguments()
    {
        WebhookMessage message = CreateMessage();
        WebhookDeliverySnapshot snapshot = new(
            message,
            DeliveryState.Pending,
            0,
            nextAttemptAt: null,
            lastError: null,
            leaseExpiresAt: null);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WebhookDeliverySnapshot(message, DeliveryState.Pending, -1, null, null, null));
        Assert.Throws<ArgumentException>(
            () => new WebhookDeliveryLease(snapshot, Guid.Empty, DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WebhookEnqueueResult((WebhookEnqueueStatus)999, snapshot));
    }

    private static WebhookMessage CreateMessage()
    {
        return new WebhookMessage(
            "webhook-123",
            "order.created",
            new Uri("https://example.test/webhooks"),
            new byte[] { 1, 2, 3 },
            "application/json");
    }
}
