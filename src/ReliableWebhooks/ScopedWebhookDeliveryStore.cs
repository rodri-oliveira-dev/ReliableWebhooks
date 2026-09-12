using Microsoft.Extensions.DependencyInjection;

namespace ReliableWebhooks;

internal sealed class ScopedWebhookDeliveryStore : IWebhookDeliveryStore
{
    private readonly IServiceScopeFactory scopeFactory;

    internal ScopedWebhookDeliveryStore(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        this.scopeFactory = scopeFactory;
    }

    public async Task<WebhookEnqueueResult> EnqueueAsync(
        WebhookMessage message,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        try
        {
            return await GetStore(scope).EnqueueAsync(message, nextAttemptAt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<WebhookDeliveryLease>> ClaimDueAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        try
        {
            return await GetStore(scope).ClaimDueAsync(now, leaseDuration, maxCount, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<WebhookDeliveryLease> RenewLeaseAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        try
        {
            return await GetStore(scope).RenewLeaseAsync(lease, now, leaseDuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task MarkSucceededAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        try
        {
            await GetStore(scope).MarkSucceededAsync(lease, completedAt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task ScheduleRetryAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        DateTimeOffset nextAttemptAt,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        try
        {
            await GetStore(scope)
                .ScheduleRetryAsync(lease, completedAt, nextAttemptAt, lastError, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task MarkPermanentlyFailedAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        try
        {
            await GetStore(scope)
                .MarkPermanentlyFailedAsync(lease, completedAt, lastError, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task DeadLetterAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        try
        {
            await GetStore(scope).DeadLetterAsync(lease, completedAt, lastError, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<WebhookDeliverySnapshot?> GetAsync(
        string webhookId,
        CancellationToken cancellationToken = default)
    {
        AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        try
        {
            return await GetStore(scope).GetAsync(webhookId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await scope.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IWebhookDeliveryStore GetStore(AsyncServiceScope scope)
    {
        return scope.ServiceProvider.GetRequiredService<IWebhookDeliveryStore>();
    }
}
