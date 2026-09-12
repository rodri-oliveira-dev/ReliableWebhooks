using Microsoft.Extensions.Hosting;

namespace ReliableWebhooks;

internal sealed class WebhookDispatcherHostedService : BackgroundService
{
    private readonly WebhookDispatcher dispatcher;

    internal WebhookDispatcherHostedService(WebhookDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        this.dispatcher = dispatcher;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return dispatcher.RunAsync(stoppingToken);
    }
}
