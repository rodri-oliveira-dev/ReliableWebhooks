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
