using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using ReliableWebhooks;
using Xunit;

namespace ReliableWebhooks.Tests;

public sealed class ObservabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InstrumentationNamesAreStablePublicContract()
    {
        Assert.Equal("ReliableWebhooks", ReliableWebhooksInstrumentation.ActivitySourceName);
        Assert.Equal("ReliableWebhooks", ReliableWebhooksInstrumentation.MeterName);
        Assert.Equal("ReliableWebhooks.DeliveryAttempt", ReliableWebhooksInstrumentation.DeliveryAttemptActivityName);
        Assert.Equal("reliablewebhooks.delivery.queued", ReliableWebhooksInstrumentation.QueuedMetricName);
        Assert.Equal("reliablewebhooks.delivery.attempted", ReliableWebhooksInstrumentation.AttemptedMetricName);
        Assert.Equal("reliablewebhooks.delivery.succeeded", ReliableWebhooksInstrumentation.SucceededMetricName);
        Assert.Equal("reliablewebhooks.delivery.retried", ReliableWebhooksInstrumentation.RetriedMetricName);
        Assert.Equal("reliablewebhooks.delivery.permanently_failed", ReliableWebhooksInstrumentation.PermanentlyFailedMetricName);
        Assert.Equal("reliablewebhooks.delivery.dead_lettered", ReliableWebhooksInstrumentation.DeadLetteredMetricName);
        Assert.Equal("reliablewebhooks.delivery.duration", ReliableWebhooksInstrumentation.DeliveryDurationMetricName);
    }

    [Fact]
    public async Task SuccessfulDeliveryEmitsStructuredLogsTraceAndMetrics()
    {
        const string webhookId = "observability-success";
        const string eventType = "observability.success";
        RecordingLogger logger = new();
        using TelemetryRecorder telemetry = new();

        await RunDeliveryAsync(
            webhookId,
            eventType,
            HttpStatusCode.NoContent,
            logger,
            retryPolicy: null);

        Activity activity = Assert.Single(
            telemetry.Activities.Where(item => GetTag(item, "webhook.id") == webhookId));
        Assert.Equal(ReliableWebhooksInstrumentation.DeliveryAttemptActivityName, activity.OperationName);
        Assert.Equal(eventType, GetTag(activity, "webhook.event_type"));
        Assert.Equal("success", GetTag(activity, "webhook.outcome"));
        Assert.Equal("204", GetTag(activity, "http.response.status_code"));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);

        Assert.Contains(logger.Entries, entry => entry.EventId.Id == 1001 && entry.Properties["WebhookId"]?.ToString() == webhookId);
        Assert.Contains(logger.Entries, entry => entry.EventId.Id == 1002 && entry.Properties["WebhookId"]?.ToString() == webhookId);
        Assert.Contains(logger.Entries, entry => entry.EventId.Id == 1003 && entry.Properties["WebhookId"]?.ToString() == webhookId);
        Assert.Contains(logger.Entries, entry => entry.EventId.Id == 1004 && entry.Properties["WebhookId"]?.ToString() == webhookId);

        AssertMeasurement(telemetry, ReliableWebhooksInstrumentation.QueuedMetricName, eventType);
        AssertMeasurement(telemetry, ReliableWebhooksInstrumentation.AttemptedMetricName, eventType);
        AssertMeasurement(telemetry, ReliableWebhooksInstrumentation.SucceededMetricName, eventType);
        AssertMeasurement(telemetry, ReliableWebhooksInstrumentation.DeliveryDurationMetricName, eventType, "success");
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3, "retry_scheduled", "reliablewebhooks.delivery.retried")]
    [InlineData(HttpStatusCode.BadRequest, 3, "permanent_failure", "reliablewebhooks.delivery.permanently_failed")]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1, "dead_letter", "reliablewebhooks.delivery.dead_lettered")]
    public async Task FailureOutcomesEmitExpectedTraceAndMetric(
        HttpStatusCode statusCode,
        int maxAttempts,
        string expectedOutcome,
        string expectedMetric)
    {
        string webhookId = $"observability-{expectedOutcome}";
        string eventType = $"observability.{expectedOutcome}";
        RecordingLogger logger = new();
        using TelemetryRecorder telemetry = new();
        DefaultWebhookRetryPolicy retryPolicy = new(
            new WebhookRetryPolicyOptions
            {
                MaxAttempts = maxAttempts,
                BaseDelay = TimeSpan.FromMinutes(1),
                MaxDelay = TimeSpan.FromMinutes(1),
                JitterFactor = 0,
            });

        await RunDeliveryAsync(webhookId, eventType, statusCode, logger, retryPolicy);

        Activity activity = Assert.Single(
            telemetry.Activities.Where(item => GetTag(item, "webhook.id") == webhookId));
        Assert.Equal(expectedOutcome, GetTag(activity, "webhook.outcome"));
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        AssertMeasurement(telemetry, expectedMetric, eventType);
        AssertMeasurement(telemetry, ReliableWebhooksInstrumentation.DeliveryDurationMetricName, eventType, expectedOutcome);
    }

    [Fact]
    public async Task TelemetryDoesNotExposePayloadSecretsSignaturesOrDestination()
    {
        const string webhookId = "observability-sensitive";
        const string eventType = "observability.sensitive";
        const string sensitiveValue = "top-secret-payload-value";
        const string signature = "v1=super-secret-signature";
        const string destinationQuery = "customer-secret-query";
        RecordingLogger logger = new();
        using TelemetryRecorder telemetry = new();

        await RunDeliveryAsync(
            webhookId,
            eventType,
            HttpStatusCode.NoContent,
            logger,
            retryPolicy: null,
            payload: Encoding.UTF8.GetBytes(sensitiveValue),
            destination: new Uri($"https://example.test/webhooks?token={destinationQuery}"),
            headers: new Dictionary<string, string> { ["X-Webhook-Signature"] = signature });

        string logs = string.Join('\n', logger.Entries.Select(entry => entry.Message));
        Activity activity = Assert.Single(
            telemetry.Activities.Where(item => GetTag(item, "webhook.id") == webhookId));
        string activityData = string.Join(
            '\n',
            activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"));
        string metricData = string.Join(
            '\n',
            telemetry.Measurements
                .Where(measurement => measurement.Tags.Any(tag => tag.Value?.ToString() == eventType))
                .SelectMany(measurement => measurement.Tags)
                .Select(tag => $"{tag.Key}={tag.Value}"));

        Assert.DoesNotContain(sensitiveValue, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(signature, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(destinationQuery, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveValue, activityData, StringComparison.Ordinal);
        Assert.DoesNotContain(signature, activityData, StringComparison.Ordinal);
        Assert.DoesNotContain(destinationQuery, activityData, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveValue, metricData, StringComparison.Ordinal);
        Assert.DoesNotContain(signature, metricData, StringComparison.Ordinal);
        Assert.DoesNotContain(destinationQuery, metricData, StringComparison.Ordinal);
        Assert.DoesNotContain(
            telemetry.Measurements.SelectMany(measurement => measurement.Tags),
            tag => string.Equals(tag.Key, "webhook.id", StringComparison.Ordinal));
    }

    private static async Task RunDeliveryAsync(
        string webhookId,
        string eventType,
        HttpStatusCode statusCode,
        RecordingLogger logger,
        IWebhookRetryPolicy? retryPolicy,
        byte[]? payload = null,
        Uri? destination = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        InMemoryWebhookDeliveryStore store = new(logger);
        WebhookMessage message = new(
            webhookId,
            eventType,
            destination ?? new Uri("https://example.test/webhooks"),
            payload ?? [1, 2, 3],
            "application/json",
            headers);
        _ = await store.EnqueueAsync(message, Now, TestContext.Current.CancellationToken);

        using HttpClient client = new(new StatusHandler(statusCode))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        WebhookHttpTransport transport = new(client);
        BlockingDelay delay = new();
        using CancellationTokenSource shutdown = new();
        WebhookDispatcher dispatcher = new(
            store,
            transport,
            retryPolicy ?? new DefaultWebhookRetryPolicy(),
            new WebhookDispatcherOptions
            {
                MaxConcurrency = 1,
                LeaseDuration = TimeSpan.FromMinutes(1),
                PollInterval = TimeSpan.FromSeconds(5),
                ShutdownGracePeriod = TimeSpan.FromSeconds(30),
                TimeProvider = new FixedTimeProvider(Now),
            },
            delay,
            logger);

        Task run = dispatcher.RunAsync(shutdown.Token);
        _ = await delay.Entered.WaitAsync(TestContext.Current.CancellationToken);
        shutdown.Cancel();
        await run;
    }

    private static void AssertMeasurement(
        TelemetryRecorder telemetry,
        string instrumentName,
        string eventType,
        string? outcome = null)
    {
        Assert.Contains(
            telemetry.Measurements,
            measurement => measurement.InstrumentName == instrumentName
                && measurement.Tags.Any(tag => tag.Key == "webhook.event_type" && tag.Value?.ToString() == eventType)
                && (outcome is null
                    || measurement.Tags.Any(tag => tag.Key == "webhook.outcome" && tag.Value?.ToString() == outcome)));
    }

    private static string? GetTag(Activity activity, string name)
    {
        return activity.TagObjects.FirstOrDefault(tag => tag.Key == name).Value?.ToString();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            this.now = now;
        }

        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class BlockingDelay : IWebhookDispatcherDelay
    {
        private readonly TaskCompletionSource<TimeSpan> entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TimeSpan> Entered => entered.Task;

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult(delay);
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode statusCode;

        public StatusHandler(HttpStatusCode statusCode)
        {
            this.statusCode = statusCode;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();

        public IReadOnlyCollection<LogEntry> Entries => entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Dictionary<string, object?> properties = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            entries.Enqueue(new LogEntry(eventId, formatter(state, exception), properties));
        }
    }

    private sealed record LogEntry(
        EventId EventId,
        string Message,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class TelemetryRecorder : IDisposable
    {
        private readonly ConcurrentQueue<Activity> activities = new();
        private readonly ConcurrentQueue<Measurement> measurements = new();
        private readonly ActivityListener activityListener;
        private readonly MeterListener meterListener;

        public TelemetryRecorder()
        {
            activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == ReliableWebhooksInstrumentation.ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => activities.Enqueue(activity),
            };
            ActivitySource.AddActivityListener(activityListener);

            meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == ReliableWebhooksInstrumentation.MeterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                },
            };
            meterListener.SetMeasurementEventCallback<long>(RecordMeasurement);
            meterListener.SetMeasurementEventCallback<double>(RecordMeasurement);
            meterListener.Start();
        }

        public IReadOnlyCollection<Activity> Activities => activities.ToArray();

        public IReadOnlyCollection<Measurement> Measurements => measurements.ToArray();

        public void Dispose()
        {
            meterListener.Dispose();
            activityListener.Dispose();
        }

        private void RecordMeasurement<T>(
            Instrument instrument,
            T measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
        {
            measurements.Enqueue(new Measurement(instrument.Name, tags.ToArray()));
        }
    }

    private sealed record Measurement(
        string InstrumentName,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);
}
