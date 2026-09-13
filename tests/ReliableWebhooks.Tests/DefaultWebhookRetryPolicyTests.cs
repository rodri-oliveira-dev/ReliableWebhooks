using ReliableWebhooks;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class DefaultWebhookRetryPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstAttemptUsesBaseDelayWithoutJitter()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(jitterFactor: 0);
        WebhookRetryContext context = new(CreateDelivery(attemptCount: 1), Now);

        WebhookRetryDecision decision = policy.GetDecision(context);

        Assert.Equal(WebhookRetryAction.Retry, decision.Action);
        Assert.Equal(Now.AddSeconds(2), decision.NextAttemptAt);
    }

    [Fact]
    public void LaterAttemptsUseExponentialBackoff()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(jitterFactor: 0);
        WebhookRetryContext context = new(CreateDelivery(attemptCount: 3), Now);

        WebhookRetryDecision decision = policy.GetDecision(context);

        Assert.Equal(Now.AddSeconds(8), decision.NextAttemptAt);
    }

    [Fact]
    public void JitterIsDeterministicWhenSourceIsInjected()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(
            jitterFactor: 0.2,
            jitterSource: new FixedJitterSource(0.5));
        WebhookRetryContext context = new(CreateDelivery(attemptCount: 1), Now);

        WebhookRetryDecision decision = policy.GetDecision(context);

        Assert.Equal(Now.AddSeconds(2.2), decision.NextAttemptAt);
    }

    [Fact]
    public void MaximumDelayCapsBackoffAndJitter()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(
            maxDelay: TimeSpan.FromSeconds(5),
            jitterFactor: 1,
            jitterSource: new FixedJitterSource(1));
        WebhookRetryContext context = new(CreateDelivery(attemptCount: 3), Now);

        WebhookRetryDecision decision = policy.GetDecision(context);

        Assert.Equal(Now.AddSeconds(5), decision.NextAttemptAt);
    }

    [Fact]
    public void LaterRetryAfterDeltaOverridesLocalDelay()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(jitterFactor: 0);
        WebhookRetryContext context = new(
            CreateDelivery(attemptCount: 1),
            Now,
            retryAfterDelay: TimeSpan.FromSeconds(12));

        WebhookRetryDecision decision = policy.GetDecision(context);

        Assert.Equal(Now.AddSeconds(12), decision.NextAttemptAt);
    }

    [Fact]
    public void LaterRetryAfterDateOverridesLocalDelay()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(jitterFactor: 0);
        WebhookRetryContext context = new(
            CreateDelivery(attemptCount: 1),
            Now,
            retryAfterDate: Now.AddSeconds(15));

        WebhookRetryDecision decision = policy.GetDecision(context);

        Assert.Equal(Now.AddSeconds(15), decision.NextAttemptAt);
    }

    [Fact]
    public void EarlierOrNegativeRetryAfterFallsBackToLocalPolicy()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(jitterFactor: 0);
        WebhookRetryContext context = new(
            CreateDelivery(attemptCount: 1),
            Now,
            retryAfterDelay: TimeSpan.FromSeconds(-10),
            retryAfterDate: Now.AddMinutes(-1));

        WebhookRetryDecision decision = policy.GetDecision(context);

        Assert.Equal(Now.AddSeconds(2), decision.NextAttemptAt);
    }

    [Fact]
    public void RetryAfterDeltaLongerThanMaximumDelayIsAuthoritative()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(
            maxDelay: TimeSpan.FromSeconds(30),
            jitterFactor: 0);
        WebhookRetryContext context = new(
            CreateDelivery(attemptCount: 1),
            Now,
            retryAfterDelay: TimeSpan.FromHours(1));

        WebhookRetryDecision decision = policy.GetDecision(context);

        Assert.Equal(Now.AddHours(1), decision.NextAttemptAt);
    }

    [Fact]
    public void RetryAfterDateLongerThanMaximumDelayIsAuthoritative()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(
            maxDelay: TimeSpan.FromSeconds(30),
            jitterFactor: 0);
        WebhookRetryContext context = new(
            CreateDelivery(attemptCount: 1),
            Now,
            retryAfterDate: Now.AddHours(2));

        WebhookRetryDecision decision = policy.GetDecision(context);

        Assert.Equal(Now.AddHours(2), decision.NextAttemptAt);
    }

    [Fact]
    public void LocalDelayStillWinsWhenRetryAfterIsEarlier()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(jitterFactor: 0);
        WebhookRetryContext context = new(
            CreateDelivery(attemptCount: 3),
            Now,
            retryAfterDelay: TimeSpan.FromSeconds(1),
            retryAfterDate: Now.AddSeconds(3));

        WebhookRetryDecision decision = policy.GetDecision(context);

        Assert.Equal(Now.AddSeconds(8), decision.NextAttemptAt);
    }

    [Fact]
    public void ExtremeRetryAfterDelaySaturatesAtMaximumRepresentableTime()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(jitterFactor: 0);
        WebhookRetryContext context = new(
            CreateDelivery(attemptCount: 1),
            DateTimeOffset.MaxValue.AddTicks(-1),
            retryAfterDelay: TimeSpan.FromDays(1));

        WebhookRetryDecision decision = policy.GetDecision(context);

        Assert.Equal(DateTimeOffset.MaxValue, decision.NextAttemptAt);
    }

    [Fact]
    public void ExhaustedAttemptsReturnDeadLetterDecision()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(maxAttempts: 3, jitterFactor: 0);
        WebhookRetryContext context = new(CreateDelivery(attemptCount: 3), Now);

        WebhookRetryDecision decision = policy.GetDecision(context);

        Assert.Equal(WebhookRetryAction.DeadLetter, decision.Action);
        Assert.False(decision.ShouldRetry);
        Assert.Null(decision.NextAttemptAt);
    }

    [Fact]
    public void InvalidJitterSourceValueIsRejected()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(
            jitterFactor: 0.2,
            jitterSource: new FixedJitterSource(1.1));
        WebhookRetryContext context = new(CreateDelivery(attemptCount: 1), Now);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => policy.GetDecision(context));

        Assert.Contains("zero through one", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeliveryWithoutStartedAttemptCannotBeScheduled()
    {
        DefaultWebhookRetryPolicy policy = CreatePolicy(jitterFactor: 0);
        WebhookRetryContext context = new(CreateDelivery(attemptCount: 0), Now);

        Assert.Throws<InvalidOperationException>(() => policy.GetDecision(context));
    }

    [Fact]
    public void ConfigurationRejectsInvalidRanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DefaultWebhookRetryPolicy(new WebhookRetryPolicyOptions { MaxAttempts = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DefaultWebhookRetryPolicy(new WebhookRetryPolicyOptions { BaseDelay = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DefaultWebhookRetryPolicy(
                new WebhookRetryPolicyOptions
                {
                    BaseDelay = TimeSpan.FromSeconds(10),
                    MaxDelay = TimeSpan.FromSeconds(5),
                }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DefaultWebhookRetryPolicy(new WebhookRetryPolicyOptions { JitterFactor = 1.1 }));
    }

    private static DefaultWebhookRetryPolicy CreatePolicy(
        int maxAttempts = 5,
        TimeSpan? maxDelay = null,
        double jitterFactor = 0.2,
        IWebhookRetryJitterSource? jitterSource = null)
    {
        WebhookRetryPolicyOptions options = new()
        {
            MaxAttempts = maxAttempts,
            BaseDelay = TimeSpan.FromSeconds(2),
            MaxDelay = maxDelay ?? TimeSpan.FromMinutes(5),
            JitterFactor = jitterFactor,
        };

        return new DefaultWebhookRetryPolicy(options, jitterSource);
    }

    private static WebhookDeliverySnapshot CreateDelivery(int attemptCount)
    {
        WebhookMessage message = new(
            "retry-policy-test",
            "order.created",
            new Uri("https://example.test/webhooks"),
            new byte[] { 1, 2, 3 },
            "application/json");

        return new WebhookDeliverySnapshot(
            message,
            DeliveryState.InProgress,
            attemptCount,
            null,
            null,
            Now.AddMinutes(1));
    }

    private sealed class FixedJitterSource : IWebhookRetryJitterSource
    {
        private readonly double value;

        internal FixedJitterSource(double value)
        {
            this.value = value;
        }

        public double NextValue()
        {
            return value;
        }
    }
}
