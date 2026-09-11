namespace ReliableWebhooks;

/// <summary>
/// Provides a process-local, non-durable implementation of <see cref="IWebhookDeliveryStore"/> for tests and samples.
/// </summary>
/// <remarks>
/// This implementation loses all data when the process exits and must not be used as durable production persistence.
/// </remarks>
public sealed class InMemoryWebhookDeliveryStore : IWebhookDeliveryStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<WebhookEnqueueResult> EnqueueAsync(
        WebhookMessage message,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (entries.TryGetValue(message.Id, out Entry? existing))
            {
                return Task.FromResult(
                    new WebhookEnqueueResult(WebhookEnqueueStatus.AlreadyExists, CreateSnapshot(existing)));
            }

            Entry entry = new(message, nextAttemptAt);
            entries.Add(message.Id, entry);

            return Task.FromResult(
                new WebhookEnqueueResult(WebhookEnqueueStatus.Enqueued, CreateSnapshot(entry)));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WebhookDeliveryLease>> ClaimDueAsync(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ValidateLeaseDuration(leaseDuration);

        if (maxCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "Claim count must be greater than zero.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            Entry[] dueEntries = entries.Values
                .Where(entry => IsDue(entry, now))
                .OrderBy(GetDueAt)
                .ThenBy(entry => entry.Message.Id, StringComparer.Ordinal)
                .Take(maxCount)
                .ToArray();

            List<WebhookDeliveryLease> leases = new(dueEntries.Length);
            DateTimeOffset expiresAt = now.Add(leaseDuration);

            foreach (Entry entry in dueEntries)
            {
                Guid token = Guid.NewGuid();
                entry.State = DeliveryState.InProgress;
                entry.AttemptCount++;
                entry.NextAttemptAt = null;
                entry.LeaseToken = token;
                entry.LeaseExpiresAt = expiresAt;

                WebhookDeliverySnapshot snapshot = CreateSnapshot(entry);
                leases.Add(new WebhookDeliveryLease(snapshot, token, expiresAt));
            }

            return Task.FromResult<IReadOnlyList<WebhookDeliveryLease>>(leases);
        }
    }

    /// <inheritdoc />
    public Task<WebhookDeliveryLease> RenewLeaseAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateLeaseDuration(leaseDuration);
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            Entry entry = GetActiveEntry(lease, now);
            DateTimeOffset requestedExpiration = now.Add(leaseDuration);
            DateTimeOffset currentExpiration = entry.LeaseExpiresAt!.Value;
            DateTimeOffset expiresAt = requestedExpiration > currentExpiration
                ? requestedExpiration
                : currentExpiration;

            entry.LeaseExpiresAt = expiresAt;
            WebhookDeliverySnapshot snapshot = CreateSnapshot(entry);

            return Task.FromResult(new WebhookDeliveryLease(snapshot, lease.Token, expiresAt));
        }
    }

    /// <inheritdoc />
    public Task MarkSucceededAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            Entry entry = GetActiveEntry(lease, completedAt);
            entry.State = DeliveryState.Succeeded;
            entry.LastError = null;
            ClearSchedulingAndLease(entry);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ScheduleRetryAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        DateTimeOffset nextAttemptAt,
        string? error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        if (nextAttemptAt < completedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(nextAttemptAt),
                nextAttemptAt,
                "The next attempt cannot be scheduled before the current attempt completed.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            Entry entry = GetActiveEntry(lease, completedAt);
            entry.State = DeliveryState.Failed;
            entry.NextAttemptAt = nextAttemptAt;
            entry.LastError = error;
            ClearLease(entry);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task MarkPermanentlyFailedAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            Entry entry = GetActiveEntry(lease, completedAt);
            entry.State = DeliveryState.PermanentlyFailed;
            entry.LastError = error;
            ClearSchedulingAndLease(entry);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeadLetterAsync(
        WebhookDeliveryLease lease,
        DateTimeOffset completedAt,
        string? error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            Entry entry = GetActiveEntry(lease, completedAt);
            entry.State = DeliveryState.DeadLettered;
            entry.LastError = error;
            ClearSchedulingAndLease(entry);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<WebhookDeliverySnapshot?> GetAsync(
        string webhookId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webhookId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            WebhookDeliverySnapshot? snapshot = entries.TryGetValue(webhookId, out Entry? entry)
                ? CreateSnapshot(entry)
                : null;

            return Task.FromResult(snapshot);
        }
    }

    private static bool IsDue(Entry entry, DateTimeOffset now)
    {
        return entry.State switch
        {
            DeliveryState.Pending or DeliveryState.Failed =>
                entry.NextAttemptAt.HasValue && entry.NextAttemptAt.Value <= now,
            DeliveryState.InProgress =>
                entry.LeaseExpiresAt.HasValue && entry.LeaseExpiresAt.Value <= now,
            _ => false,
        };
    }

    private static DateTimeOffset GetDueAt(Entry entry)
    {
        return entry.State == DeliveryState.InProgress
            ? entry.LeaseExpiresAt!.Value
            : entry.NextAttemptAt!.Value;
    }

    private Entry GetActiveEntry(WebhookDeliveryLease lease, DateTimeOffset now)
    {
        string webhookId = lease.Delivery.Message.Id;

        if (!entries.TryGetValue(webhookId, out Entry? entry))
        {
            throw CreateConcurrencyException(webhookId, "The delivery no longer exists in the store.");
        }

        if (entry.State != DeliveryState.InProgress)
        {
            throw CreateConcurrencyException(
                webhookId,
                $"The delivery is not in progress; current state is {entry.State}.");
        }

        if (!entry.LeaseToken.HasValue || entry.LeaseToken.Value != lease.Token)
        {
            throw CreateConcurrencyException(webhookId, "The lease token is stale and no longer owns the delivery.");
        }

        if (!entry.LeaseExpiresAt.HasValue || entry.LeaseExpiresAt.Value <= now)
        {
            throw CreateConcurrencyException(webhookId, "The lease has expired and can no longer update the delivery.");
        }

        return entry;
    }

    private static WebhookDeliveryStoreConcurrencyException CreateConcurrencyException(
        string webhookId,
        string reason)
    {
        return new WebhookDeliveryStoreConcurrencyException(
            webhookId,
            $"Webhook delivery '{webhookId}' update was rejected. {reason}");
    }

    private static WebhookDeliverySnapshot CreateSnapshot(Entry entry)
    {
        return new WebhookDeliverySnapshot(
            entry.Message,
            entry.State,
            entry.AttemptCount,
            entry.NextAttemptAt,
            entry.LastError,
            entry.LeaseExpiresAt);
    }

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(leaseDuration),
                leaseDuration,
                "Lease duration must be greater than zero.");
        }
    }

    private static void ClearSchedulingAndLease(Entry entry)
    {
        entry.NextAttemptAt = null;
        ClearLease(entry);
    }

    private static void ClearLease(Entry entry)
    {
        entry.LeaseToken = null;
        entry.LeaseExpiresAt = null;
    }

    private sealed class Entry
    {
        internal Entry(WebhookMessage message, DateTimeOffset nextAttemptAt)
        {
            Message = message;
            State = DeliveryState.Pending;
            NextAttemptAt = nextAttemptAt;
        }

        internal WebhookMessage Message { get; }

        internal DeliveryState State { get; set; }

        internal int AttemptCount { get; set; }

        internal DateTimeOffset? NextAttemptAt { get; set; }

        internal string? LastError { get; set; }

        internal Guid? LeaseToken { get; set; }

        internal DateTimeOffset? LeaseExpiresAt { get; set; }
    }
}
