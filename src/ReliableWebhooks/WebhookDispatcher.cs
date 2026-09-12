using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private const string SuccessOutcome = "success";
    private const string RetryOutcome = "retry_scheduled";
    private const string PermanentFailureOutcome = "permanent_failure";
    private const string DeadLetterOutcome = "dead_letter";
    private const string CanceledOutcome = "canceled";
    private const string LeaseLostOutcome = "lease_lost";
    private const string UnexpectedFailureOutcome = "unexpected_failure";

    private readonly IWebhookDeliveryStore store;
    private readonly IWebhookDeliveryTransport transport;
    private readonly IWebhookRetryPolicy retryPolicy;
    private readonly IWebhookDispatcherDelay delay;
    private readonly ILogger logger;
    private readonly int maxConcurrency;
    private readonly TimeSpan leaseDuration;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan shutdownGracePeriod;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Initializes a new webhook dispatcher without structured logging.
    /// </summary>
    /// <param name="store">The delivery store used for claims and state transitions.</param>
    /// <param name="transport">The transport used for exactly one delivery attempt per claimed lease.</param>
    /// <param name="retryPolicy">The retry policy used for retryable failures.</param>
    /// <param name="options">Optional dispatcher configuration.</param>
    /// <param name="delay">Optional polling delay implementation.</param>
    public WebhookDispatcher(
        IWebhookDeliveryStore store,
        IWebhookDeliveryTransport transport,
        IWebhookRetryPolicy retryPolicy,
        WebhookDispatcherOptions? options = null,
        IWebhookDispatcherDelay? delay = null)
        : this(store, transport, retryPolicy, options, delay, NullLogger.Instance)
    {
    }

    /// <summary>
    /// Initializes a new webhook dispatcher with structured lifecycle logging.
    /// </summary>
    /// <param name="store">The delivery store used for claims and state transitions.</param>
    /// <param name="transport">The transport used for exactly one delivery attempt per claimed lease.</param>
    /// <param name="retryPolicy">The retry policy used for retryable failures.</param>
    /// <param name="options">Optional dispatcher configuration.</param>
    /// <param name="delay">Optional polling delay implementation.</param>
    /// <param name="logger">The logger that receives safe structured lifecycle events.</param>
    /// <exception cref="ArgumentNullException">A required dependency, logger, or time provider is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dispatcher option is outside its supported range.</exception>
    public WebhookDispatcher(
        IWebhookDeliveryStore store,
        IWebhookDeliveryTransport transport,
        IWebhookRetryPolicy retryPolicy,
        WebhookDispatcherOptions? options,
        IWebhookDispatcherDelay? delay,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(logger);

        options ??= new WebhookDispatcherOptions();
        ValidateOptions(options);

        this.store = store;
        this.transport = transport;
        this.retryPolicy = retryPolicy;
        this.logger = logger;
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
        ExceptionDispatchInfo? failure = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Exception? completedFailure = await ObserveCompletedAsync(inFlight).ConfigureAwait(false);
                if (completedFailure is not null)
                {
                    throw completedFailure;
                }

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
                    ReliableWebhooksLog.Claimed(
                        logger,
                        lease.Delivery.Message.Id,
                        lease.Delivery.Message.EventType,
                        lease.Delivery.AttemptCount);
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
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            Exception? drainFailure = await DrainAsync(inFlight, attemptCancellation).ConfigureAwait(false);
            if (failure is null && drainFailure is not null)
            {
                failure = ExceptionDispatchInfo.Capture(drainFailure);
            }
        }

        failure?.Throw();
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
        WebhookMessage message = lease.Delivery.Message;
        int attempt = lease.Delivery.AttemptCount;
        string outcome = UnexpectedFailureOutcome;
        long startedAt = Stopwatch.GetTimestamp();

        using Activity? activity = ReliableWebhooksInstrumentation.ActivitySource.StartActivity(
            ReliableWebhooksInstrumentation.DeliveryAttemptActivityName,
            ActivityKind.Producer);
        activity?.SetTag(ReliableWebhooksInstrumentation.WebhookIdTagName, message.Id);
        activity?.SetTag(ReliableWebhooksInstrumentation.EventTypeTagName, message.EventType);
        activity?.SetTag(ReliableWebhooksInstrumentation.AttemptTagName, attempt);

        ReliableWebhooksInstrumentation.Attempted.Add(
            1,
            CreateEventTypeTags(message.EventType));
        ReliableWebhooksLog.AttemptStarted(logger, message.Id, message.EventType, attempt);

        try
        {
            WebhookDeliveryResult result = await transport.SendAsync(
                message,
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            DateTimeOffset completedAt = timeProvider.GetUtcNow();

            if (result.StatusCode is int statusCode)
            {
                activity?.SetTag(ReliableWebhooksInstrumentation.HttpStatusCodeTagName, statusCode);
            }

            switch (result.Outcome)
            {
                case WebhookDeliveryOutcome.Success:
                    await store.MarkSucceededAsync(
                        lease,
                        completedAt,
                        cancellationToken).ConfigureAwait(false);
                    outcome = SuccessOutcome;
                    ReliableWebhooksInstrumentation.Succeeded.Add(1, CreateEventTypeTags(message.EventType));
                    ReliableWebhooksLog.AttemptSucceeded(logger, message.Id, message.EventType, attempt);
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    break;

                case WebhookDeliveryOutcome.PermanentFailure:
                    await store.MarkPermanentlyFailedAsync(
                        lease,
                        completedAt,
                        DescribeFailure(result),
                        cancellationToken).ConfigureAwait(false);
                    outcome = PermanentFailureOutcome;
                    ReliableWebhooksInstrumentation.PermanentlyFailed.Add(1, CreateEventTypeTags(message.EventType));
                    ReliableWebhooksLog.PermanentlyFailed(logger, message.Id, message.EventType, attempt);
                    activity?.SetStatus(ActivityStatusCode.Error, PermanentFailureOutcome);
                    break;

                case WebhookDeliveryOutcome.RetryableFailure:
                    outcome = await PersistRetryableFailureAsync(
                        lease,
                        result,
                        completedAt,
                        cancellationToken).ConfigureAwait(false);
                    activity?.SetStatus(ActivityStatusCode.Error, outcome);
                    break;

                default:
                    throw new InvalidOperationException("The webhook transport returned an undefined delivery outcome.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = CanceledOutcome;
            ReliableWebhooksLog.AttemptCanceled(logger, message.Id, message.EventType, attempt);
            // Cancellation intentionally leaves the lease in progress so it can expire and be reclaimed safely.
        }
        catch (WebhookDeliveryStoreConcurrencyException)
        {
            outcome = LeaseLostOutcome;
            ReliableWebhooksLog.LeaseOwnershipLost(logger, message.Id, message.EventType, attempt);
            // Lease ownership was lost or expired. A stale worker must not overwrite the current owner.
        }
        catch (Exception exception)
        {
            outcome = UnexpectedFailureOutcome;
            string exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            activity?.SetTag(ReliableWebhooksInstrumentation.ErrorTypeTagName, exceptionType);
            activity?.SetStatus(ActivityStatusCode.Error, UnexpectedFailureOutcome);
            ReliableWebhooksLog.AttemptFailedUnexpectedly(
                logger,
                message.Id,
                message.EventType,
                attempt,
                exceptionType);
            throw;
        }
        finally
        {
            activity?.SetTag(ReliableWebhooksInstrumentation.OutcomeTagName, outcome);
            TagList durationTags = CreateEventTypeTags(message.EventType);
            durationTags.Add(ReliableWebhooksInstrumentation.OutcomeTagName, outcome);
            ReliableWebhooksInstrumentation.DeliveryDuration.Record(
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                durationTags);
        }
    }

    private async Task<string> PersistRetryableFailureAsync(
        WebhookDeliveryLease lease,
        WebhookDeliveryResult result,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken)
    {
        WebhookRetryDecision decision = retryPolicy.GetDecision(
            WebhookRetryContext.FromResult(lease.Delivery, result, completedAt));
        string? lastError = DescribeFailure(result);
        WebhookMessage message = lease.Delivery.Message;
        int attempt = lease.Delivery.AttemptCount;

        if (decision.ShouldRetry)
        {
            DateTimeOffset nextAttemptAt = decision.NextAttemptAt!.Value;
            await store.ScheduleRetryAsync(
                lease,
                completedAt,
                nextAttemptAt,
                lastError,
                cancellationToken).ConfigureAwait(false);
            ReliableWebhooksInstrumentation.Retried.Add(1, CreateEventTypeTags(message.EventType));
            ReliableWebhooksLog.RetryScheduled(
                logger,
                message.Id,
                message.EventType,
                attempt,
                nextAttemptAt);
            return RetryOutcome;
        }

        await store.DeadLetterAsync(
            lease,
            completedAt,
            lastError,
            cancellationToken).ConfigureAwait(false);
        ReliableWebhooksInstrumentation.DeadLettered.Add(1, CreateEventTypeTags(message.EventType));
        ReliableWebhooksLog.DeadLettered(logger, message.Id, message.EventType, attempt);
        return DeadLetterOutcome;
    }

    private async Task<Exception?> DrainAsync(
        HashSet<Task> inFlight,
        CancellationTokenSource attemptCancellation)
    {
        if (inFlight.Count == 0)
        {
            return null;
        }

        Task allInFlight = Task.WhenAll(inFlight);

        if (shutdownGracePeriod > TimeSpan.Zero)
        {
            using CancellationTokenSource graceCancellation = new();
            Task grace = delay.DelayAsync(shutdownGracePeriod, graceCancellation.Token);
            Task completed = await Task.WhenAny(allInFlight, grace).ConfigureAwait(false);

            if (completed == allInFlight)
            {
                graceCancellation.Cancel();
                await grace.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                await allInFlight.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                inFlight.Clear();
                return GetTaskFailure(allInFlight);
            }

            await grace.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Exception? graceFailure = GetTaskFailure(grace);

            attemptCancellation.Cancel();
            await allInFlight.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            inFlight.Clear();

            return graceFailure ?? GetTaskFailure(allInFlight);
        }

        attemptCancellation.Cancel();
        await allInFlight.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        inFlight.Clear();
        return GetTaskFailure(allInFlight);
    }

    private static async Task<Exception?> ObserveCompletedAsync(HashSet<Task> inFlight)
    {
        if (inFlight.Count == 0)
        {
            return null;
        }

        Exception? failure = null;
        Task[] completed = inFlight.Where(task => task.IsCompleted).ToArray();
        foreach (Task task in completed)
        {
            _ = inFlight.Remove(task);
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            failure ??= GetTaskFailure(task);
        }

        return failure;
    }

    private static async Task WaitForAnyAsync(
        HashSet<Task> inFlight,
        CancellationToken cancellationToken)
    {
        _ = await Task.WhenAny(inFlight).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Exception? GetTaskFailure(Task task)
    {
        if (task.IsFaulted)
        {
            return task.Exception.InnerExceptions.Count == 1
                ? task.Exception.InnerException
                : task.Exception;
        }

        return task.IsCanceled
            ? new TaskCanceledException(task)
            : null;
    }

    private static TagList CreateEventTypeTags(string eventType)
    {
        TagList tags = default;
        tags.Add(ReliableWebhooksInstrumentation.EventTypeTagName, eventType);
        return tags;
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
