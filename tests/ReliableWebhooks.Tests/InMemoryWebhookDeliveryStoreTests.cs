using ReliableWebhooks;
using ReliableWebhooks.Tests.Conformance;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class InMemoryWebhookDeliveryStoreTests : WebhookDeliveryStoreConformanceTests
{
    protected override IWebhookDeliveryStore CreateStore()
    {
        return new InMemoryWebhookDeliveryStore();
    }

    [Fact]
    public async Task ScheduleRetryRejectsNextAttemptBeforeCompletion()
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ClaimDueRejectsNonPositiveMaximumCount(int maxCount)
    {
        InMemoryWebhookDeliveryStore store = new();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ClaimDueAsync(
                Now,
                LeaseDuration,
                maxCount,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClaimDueRejectsNonPositiveLeaseDuration()
    {
        InMemoryWebhookDeliveryStore store = new();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => store.ClaimDueAsync(
                Now,
                TimeSpan.Zero,
                1,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvalidCustomHeaderCannotBecomePersistedDelivery()
    {
        InMemoryWebhookDeliveryStore store = new();
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        Assert.Throws<ArgumentException>(
            () => new WebhookMessage(
                "poison-header",
                "order.created",
                new Uri("https://example.test/webhooks"),
                new byte[] { 1 },
                "application/json",
                new Dictionary<string, string>
                {
                    ["X-Test"] = "first\rsecond",
                }));

        Assert.Null(await store.GetAsync("poison-header", cancellationToken));
    }
}
