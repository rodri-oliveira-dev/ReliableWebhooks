using System.Diagnostics;
using System.Diagnostics.Metrics;
using ReliableWebhooks;

namespace ReliableWebhooks.Sample;

internal sealed class SampleTelemetry : IDisposable
{
    private readonly ActivityListener activityListener;
    private readonly MeterListener meterListener;

    private SampleTelemetry(ActivityListener activityListener, MeterListener meterListener)
    {
        this.activityListener = activityListener;
        this.meterListener = meterListener;
    }

    public static SampleTelemetry Start()
    {
        ActivityListener activityListener = new()
        {
            ShouldListenTo = static source =>
                string.Equals(
                    source.Name,
                    ReliableWebhooksInstrumentation.ActivitySourceName,
                    StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = static activity =>
                Console.WriteLine($"trace: {activity.DisplayName} [{activity.Status}]")
        };
        ActivitySource.AddActivityListener(activityListener);

        MeterListener meterListener = new();
        meterListener.InstrumentPublished = static (instrument, listener) =>
        {
            if (string.Equals(
                    instrument.Meter.Name,
                    ReliableWebhooksInstrumentation.MeterName,
                    StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>(static (instrument, measurement, _, _) =>
            Console.WriteLine($"metric: {instrument.Name}={measurement}"));
        meterListener.SetMeasurementEventCallback<double>(static (instrument, measurement, _, _) =>
            Console.WriteLine($"metric: {instrument.Name}={measurement:F2}"));
        meterListener.Start();

        return new SampleTelemetry(activityListener, meterListener);
    }

    public void Dispose()
    {
        meterListener.Dispose();
        activityListener.Dispose();
    }
}
