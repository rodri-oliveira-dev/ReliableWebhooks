using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ReliableWebhooks;

/// <summary>
/// Exposes the stable names used by ReliableWebhooks diagnostics so consumers can configure telemetry without magic strings.
/// </summary>
public static class ReliableWebhooksInstrumentation
{
    /// <summary>The canonical <see cref="ActivitySource"/> name.</summary>
    public const string ActivitySourceName = "ReliableWebhooks";

    /// <summary>The canonical <see cref="Meter"/> name.</summary>
    public const string MeterName = "ReliableWebhooks";

    /// <summary>The activity name emitted for a single webhook delivery attempt.</summary>
    public const string DeliveryAttemptActivityName = "ReliableWebhooks.DeliveryAttempt";

    /// <summary>Total number of newly queued webhook deliveries.</summary>
    public const string QueuedMetricName = "reliablewebhooks.delivery.queued";

    /// <summary>Total number of webhook delivery attempts started.</summary>
    public const string AttemptedMetricName = "reliablewebhooks.delivery.attempted";

    /// <summary>Total number of successful webhook deliveries.</summary>
    public const string SucceededMetricName = "reliablewebhooks.delivery.succeeded";

    /// <summary>Total number of webhook deliveries scheduled for retry.</summary>
    public const string RetriedMetricName = "reliablewebhooks.delivery.retried";

    /// <summary>Total number of webhook deliveries that failed permanently.</summary>
    public const string PermanentlyFailedMetricName = "reliablewebhooks.delivery.permanently_failed";

    /// <summary>Total number of webhook deliveries moved to dead letter.</summary>
    public const string DeadLetteredMetricName = "reliablewebhooks.delivery.dead_lettered";

    /// <summary>Duration in milliseconds of webhook delivery attempts.</summary>
    public const string DeliveryDurationMetricName = "reliablewebhooks.delivery.duration";

    internal const string WebhookIdTagName = "webhook.id";
    internal const string EventTypeTagName = "webhook.event_type";
    internal const string AttemptTagName = "webhook.attempt";
    internal const string OutcomeTagName = "webhook.outcome";
    internal const string HttpStatusCodeTagName = "http.response.status_code";
    internal const string ErrorTypeTagName = "error.type";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    internal static readonly Meter Meter = new(MeterName);

    internal static readonly Counter<long> Queued = Meter.CreateCounter<long>(
        QueuedMetricName,
        unit: "{delivery}",
        description: "Number of newly queued webhook deliveries.");

    internal static readonly Counter<long> Attempted = Meter.CreateCounter<long>(
        AttemptedMetricName,
        unit: "{attempt}",
        description: "Number of webhook delivery attempts started.");

    internal static readonly Counter<long> Succeeded = Meter.CreateCounter<long>(
        SucceededMetricName,
        unit: "{delivery}",
        description: "Number of webhook deliveries completed successfully.");

    internal static readonly Counter<long> Retried = Meter.CreateCounter<long>(
        RetriedMetricName,
        unit: "{delivery}",
        description: "Number of webhook deliveries scheduled for retry.");

    internal static readonly Counter<long> PermanentlyFailed = Meter.CreateCounter<long>(
        PermanentlyFailedMetricName,
        unit: "{delivery}",
        description: "Number of webhook deliveries that failed permanently.");

    internal static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        DeadLetteredMetricName,
        unit: "{delivery}",
        description: "Number of webhook deliveries moved to dead letter.");

    internal static readonly Histogram<double> DeliveryDuration = Meter.CreateHistogram<double>(
        DeliveryDurationMetricName,
        unit: "ms",
        description: "Duration of webhook delivery attempts in milliseconds.");
}
