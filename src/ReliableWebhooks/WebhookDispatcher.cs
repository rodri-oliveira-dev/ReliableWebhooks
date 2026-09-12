namespace ReliableWebhooks;

/// <summary>
/// Continuously claims due webhook deliveries, processes them with bounded concurrency, and persists outcomes.
/// </summary>
/// <remarks>
/// The dispatcher is hosting-framework agnostic. Dependency-injection and hosted-service integration can wrap
/// this type without changing its delivery semantics.
/// </remarks>
public sealed class WebhookDispatcher
{
    private readonly IWebhookDeliveryStore store;
    private readonly IWebhookDeliveryTransport transport;
    private readonly IWebhookRetryPolicy retryPolicy;
    private readonly IWebhookDispatcherDelay delay;
    private readonly int maxConcurrency;
    private readonly TimeSpan leaseDuration;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan shutdownGracePeriod;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Initializes a new webhook dispatcher.
    /// </summary>
    /// <param name="store">The delivery store used for claims and state transitions.</param>
    /// <param name="transport">The transport used for exactly one delivery attempt per claimed lease.</param>
    /// <param name="retryPolicy">The retry policy used for retryable failures.</param>
    /// <param name="options">Optional dispatcher configuration.</param>
    /// <param name="delay">Optional polling delay implementation.</param>
    /// <exception cref="ArgumentNullException">A required dependency or time provider is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dispatcher option is outside its supported range.</exception>
    public WebhookDispatcher(
        IWebhookDeliveryStore store,
        IWebhookDeliveryTransport transport,
        IWebhookRetryPolicy retryPolicy,
        WebhookDispatcherOptions? options = null,
        IWebhookDispatcherDelay? delay = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(retryPolicy);

        options ??= new WebhookDispatcherOptions();
        ValidateOptions(options);

        this.store = store;
        this.transport = transport;
        this.retryPolicy = retryPolicy;
        maxConcurrency = options.MaxConcurrency;
        leaseDuration = options.LeaseDuration;
        pollInterval = options.PollInterval;
        shutdownGracePeriod = options.ShutdownGracePeriod;
        timeProvider = options.TimeProvider;
        this.delay = delay ?? new TimeProviderDispatcherDelay(timeProvider);
    }

    /// <summary>
    /// Runs the dispatcher until cancellation is requested.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token that stops new claims. In-flight deliveries are allowed to finish within the configured grace period.
    /// </param>
    /// <returns>A task representing the dispatcher lifetime.</returns>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        HashSet<Task> inFlight = [];
        using CancellationTokenSource attemptCancellation = new();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await ObserveCompletedAsync(inFlight).ConfigureAwait(false);

                int availableSlots = maxConcurrency - inFlight.Count;
                if (availableSlots == 0)
                {
                    await WaitForAnyAsync(inFlight, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                IReadOnlyList<WebhookDeliveryLease> leases = await store.ClaimDueAsync(
                    timeProvider.GetUtcNow(),
                    leaseDuration,
                    availableSlots,
                    cancellationToken).ConfigureAwait(false);

                foreach (WebhookDeliveryLease lease in leases)
                {
                    inFlight.Add(ProcessLeaseAsync(lease, attemptCancellation.Token));
                }

                if (leases.Count == 0)
                {
                    await delay.DelayAsync(pollInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Requested shutdown stops polling and moves to the graceful drain below.
        }
        finally
        {
            await DrainAsync(inFlight, attemptCancellation).ConfigureAwait(false);
        }
    }

    private static void ValidateOptions(WebhookDispatcherOptions options)
    {
        if (options.MaxConcurrency < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxConcurrency,
                "Maximum concurrency must be greater than zero.");
        }

        if (options.LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.LeaseDuration,
                "Lease duration must be greater than zero.");
        }

        if (options.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.PollInterval,
                "Poll interval must be greater than zero.");
        }

        if (options.ShutdownGracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ShutdownGracePeriod,
                "Shutdown grace period cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(options.TimeProvider);
    }

    private async Task ProcessLeaseAsync(
        WebhookDeliveryLease lease,
        CancellationToken cancellationToken)
    {
        try
        {
            WebhookDeliveryResult result = await transport.SendAsync(
                lease.Delivery.Message,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset completedAt = timeProvider.GetUtcNow();

            switch (result.Outcome)
            {
                case WebhookDeliveryOutcome.Success:
                    await store.MarkSucceededAsync(
                        lease,
                        completedAt,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case WebhookDeliveryOutcome.PermanentFailure:
                    await store.MarkPermanentlyFailedAsync(
                        lease,
                        completedAt,
                        DescribeFailure(result),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case WebhookDeliveryOutcome.RetryableFailure:
                    await PersistRetryableFailureAsync(
                        lease,
                        result,
                        completedAt,
                        cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException("The webhook transport returned an undefined delivery outcome.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation intentionally leaves the lease in progress so it can expire and be reclaimed safely.
        }
        catch (WebhookDeliveryStoreConcurrencyException)
        {
            // Lease ownership was lost or expired. A stale worker must not overwrite the current owner.
        }
    }

    private async Task PersistRetryableFailureAsync(
        WebhookDeliveryLease lease,
        WebhookDeliveryResult result,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        WebhookRetryDecision decision = retryPolicy.GetDecision(
            WebhookRetryContext.FromResult(lease.Delivery, result, completedAt));
        string? lastError = DescribeFailure(result);

        if (decision.ShouldRetry)
        {
            await store.ScheduleRetryAsync(
                lease,
                completedAt,
                decision.NextAttemptAt!.Value,
                lastError,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await store.DeadLetterAsync(
            lease,
            completedAt,
            lastError,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainAsync(
        HashSet<Task> inFlight,
        CancellationTokenSource attemptCancellation)
    {
        await ObserveCompletedAsync(inFlight).ConfigureAwait(false);
        if (inFlight.Count == 0)
        {
            return;
        }

        if (shutdownGracePeriod > TimeSpan.Zero)
        {
            using CancellationTokenSource graceCancellation = new();
            Task allInFlight = Task.WhenAll(inFlight);
            Task grace = delay.DelayAsync(shutdownGracePeriod, graceCancellation.Token);
            Task completed = await Task.WhenAny(allInFlight, grace).ConfigureAwait(false);

            if (completed == allInFlight)
            {
                graceCancellation.Cancel();
                await ObserveCanceledDelayAsync(grace, graceCancellation.Token).ConfigureAwait(false);
                await allInFlight.ConfigureAwait(false);
                inFlight.Clear();
                return;
            }

            await grace.ConfigureAwait(false);
        }

        attemptCancellation.Cancel();
        await Task.WhenAll(inFlight).ConfigureAwait(false);
        inFlight.Clear();
    }

    private static async Task ObserveCanceledDelayAsync(Task delayTask, CancellationToken cancellationToken)
    {
        try
        {
            await delayTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task ObserveCompletedAsync(HashSet<Task> inFlight)
    {
        if (inFlight.Count == 0)
        {
            return;
        }

        Task[] completed = inFlight.Where(task => task.IsCompleted).ToArray();
        foreach (Task task in completed)
        {
            _ = inFlight.Remove(task);
            await task.ConfigureAwait(false);
        }
    }

    private static async Task WaitForAnyAsync(
        HashSet<Task> inFlight,
        CancellationToken cancellationToken)
    {
        Task completed = await Task.WhenAny(inFlight).WaitAsync(cancellationToken).ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }

    private static string? DescribeFailure(WebhookDeliveryResult result)
    {
        if (result.StatusCode is int statusCode)
        {
            return $"HTTP {statusCode}";
        }

        return result.FailureKind switch
        {
            WebhookTransportFailureKind.Network => "Network failure",
            WebhookTransportFailureKind.Timeout => "Attempt timeout",
            _ => null,
        };
    }

    private sealed class TimeProviderDispatcherDelay : IWebhookDispatcherDelay
    {
        private readonly TimeProvider timeProvider;

        internal TimeProviderDispatcherDelay(TimeProvider timeProvider)
        {
            this.timeProvider = timeProvider;
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            return Task.Delay(delay, timeProvider, cancellationToken);
        }
    }
}
