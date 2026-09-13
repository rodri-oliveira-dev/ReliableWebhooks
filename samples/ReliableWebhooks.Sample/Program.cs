using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReliableWebhooks;

namespace ReliableWebhooks.Sample;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(SampleScenario.BaseAddress);

        builder.Services.AddSingleton(
            static _ => new SampleSigningSecret(RandomNumberGenerator.GetBytes(32)));
        builder.Services.AddSingleton<IWebhookSigningSecretProvider, SampleSigningSecretProvider>();
        builder.Services.AddSingleton<IWebhookRequestSigner, HmacSha256WebhookRequestSigner>();

        builder.Services.AddSingleton<InMemoryWebhookDeliveryStore>();
        builder.Services.AddSingleton<IWebhookDeliveryStore>(static serviceProvider =>
            new InstrumentedWebhookDeliveryStore(
                serviceProvider.GetRequiredService<InMemoryWebhookDeliveryStore>(),
                serviceProvider.GetRequiredService<ILogger<InstrumentedWebhookDeliveryStore>>()));

        ReliableWebhooksBuilder webhooks = builder.Services.AddReliableWebhooks(options =>
        {
            options.Dispatcher.MaxConcurrency = 2;
            options.Dispatcher.LeaseDuration = TimeSpan.FromSeconds(5);
            options.Dispatcher.PollInterval = TimeSpan.FromMilliseconds(50);
            options.Dispatcher.ShutdownGracePeriod = TimeSpan.FromSeconds(5);
            options.Retry = new WebhookRetryPolicyOptions
            {
                MaxAttempts = 3,
                BaseDelay = TimeSpan.FromMilliseconds(250),
                MaxDelay = TimeSpan.FromSeconds(1),
                JitterFactor = 0,
            };
            options.Transport = new WebhookHttpTransportOptions
            {
                AttemptTimeout = TimeSpan.FromSeconds(3),
                AllowInsecureHttp = true,
            };
        });

        webhooks.AddHostedDispatcher();
        builder.Services.AddSingleton<ReceiverState>();
        builder.Services.AddHostedService<SampleScenario>();

        WebApplication app = builder.Build();
        using SampleTelemetry telemetry = SampleTelemetry.Start();

        app.MapPost("/receiver/{behavior}", ReceiverEndpoint.HandleAsync);

        await app.RunAsync().ConfigureAwait(false);
    }
}
