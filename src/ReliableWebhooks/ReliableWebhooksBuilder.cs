using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ReliableWebhooks;

/// <summary>
/// Exposes optional integration features after the core ReliableWebhooks services are registered.
/// </summary>
public sealed class ReliableWebhooksBuilder
{
    internal ReliableWebhooksBuilder(
        IServiceCollection services,
        IHttpClientBuilder httpClientBuilder)
    {
        Services = services;
        HttpClientBuilder = httpClientBuilder;
    }

    /// <summary>
    /// Gets the service collection being configured.
    /// </summary>
    public IServiceCollection Services
    {
        get;
    }

    /// <summary>
    /// Gets the named HTTP client builder used by the default webhook transport.
    /// </summary>
    /// <remarks>
    /// The default registration disables automatic redirects and disables the <see cref="HttpClient.Timeout"/>
    /// so <see cref="WebhookHttpTransportOptions.AttemptTimeout"/> remains the authoritative attempt timeout.
    /// Consumers can add delegating handlers or additional client configuration through this builder.
    /// </remarks>
    public IHttpClientBuilder HttpClientBuilder
    {
        get;
    }

    /// <summary>
    /// Registers the webhook dispatcher as a hosted background service.
    /// </summary>
    /// <returns>The current builder.</returns>
    public ReliableWebhooksBuilder AddHostedDispatcher()
    {
        Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, WebhookDispatcherHostedService>());

        return this;
    }
}
