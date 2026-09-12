using System.Text.Json;
using Microsoft.Extensions.Hosting;
using ReliableWebhooks;

namespace ReliableWebhooks.Sample;

internal sealed class SampleScenario : BackgroundService
{
    internal const string BaseAddress = "http://127.0.0.1:5080";

    private static readonly string[] WebhookIds =
    [
        "sample-success",
        "sample-retry",
        "sample-dead-letter",
    ];

    private readonly IHostApplicationLifetime applicationLifetime;
    private readonly IWebhookEnqueueService enqueueService;
    private readonly IWebhookDeliveryStore store;

    public SampleScenario(
        IHostApplicationLifetime applicationLifetime,
        IWebhookEnqueueService enqueueService,
        IWebhookDeliveryStore store)
    {
        this.applicationLifetime = applicationLifetime;
        this.enqueueService = enqueueService;
        this.store = store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await WaitForApplicationStartedAsync(stoppingToken).ConfigureAwait(false);

        await EnqueueAsync("sample-success", "success", stoppingToken).ConfigureAwait(false);
        await EnqueueAsync("sample-retry", "retry", stoppingToken).ConfigureAwait(false);
        await EnqueueAsync("sample-dead-letter", "dead-letter", stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            List<WebhookDeliverySnapshot> snapshots = new(WebhookIds.Length);

            foreach (string webhookId in WebhookIds)
            {
                WebhookDeliverySnapshot? snapshot = await store
                    .GetAsync(webhookId, stoppingToken)
                    .ConfigureAwait(false);

                if (snapshot is not null)
                {
                    snapshots.Add(snapshot);
                }
            }

            if (snapshots.Count == WebhookIds.Length && snapshots.All(static snapshot => snapshot.IsTerminal))
            {
                Console.WriteLine();
                Console.WriteLine("Final delivery states:");

                foreach (WebhookDeliverySnapshot snapshot in snapshots)
                {
                    Console.WriteLine(
                        $"- {snapshot.Message.Id}: {snapshot.State} after {snapshot.AttemptCount} attempt(s)");
                }

                applicationLifetime.StopApplication();
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task EnqueueAsync(
        string webhookId,
        string behavior,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            WebhookId = webhookId,
            Behavior = behavior,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        WebhookMessage message = new(
            webhookId,
            $"sample.{behavior}",
            new Uri($"{BaseAddress}/receiver/{behavior}", UriKind.Absolute),
            payload,
            "application/json");

        await enqueueService.EnqueueAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = applicationLifetime.ApplicationStarted.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            started);

        await started.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
