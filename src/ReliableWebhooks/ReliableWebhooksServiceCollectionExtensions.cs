using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ReliableWebhooks;

/// <summary>
/// Provides dependency-injection registration for ReliableWebhooks.
/// </summary>
public static class ReliableWebhooksServiceCollectionExtensions
{
    private const string DefaultHttpClientName = "ReliableWebhooks";

    /// <summary>
    /// Registers the default ReliableWebhooks services using standard Microsoft dependency injection.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">Optional configuration for dispatcher, retry, transport, and signing behavior.</param>
    /// <returns>A builder that exposes optional hosted-dispatcher and HTTP-client configuration.</returns>
    /// <remarks>
    /// A delivery store is intentionally not registered by default. Applications must register an
    /// <see cref="IWebhookDeliveryStore"/> appropriate for their durability requirements. The built-in
    /// <see cref="InMemoryWebhookDeliveryStore"/> is intended only for tests and samples.
    /// </remarks>
    public static ReliableWebhooksBuilder AddReliableWebhooks(
        this IServiceCollection services,
        Action<ReliableWebhooksOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        OptionsBuilder<ReliableWebhooksOptions> optionsBuilder =
            services.AddOptions<ReliableWebhooksOptions>();

        if (configure is not null)
        {
            _ = optionsBuilder.Configure(configure);
        }

        _ = optionsBuilder.ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<ReliableWebhooksOptions>, ReliableWebhooksOptionsValidator>());

        services.TryAddSingleton<IWebhookHttpResponseClassifier, DefaultWebhookHttpResponseClassifier>();
        services.TryAddSingleton<IWebhookRetryPolicy>(CreateRetryPolicy);
        services.TryAddSingleton<IWebhookDeliveryTransport>(CreateTransport);
        services.TryAddSingleton<IWebhookEnqueueService>(CreateEnqueueService);
        services.TryAddSingleton(CreateDispatcher);

        IHttpClientBuilder httpClientBuilder = services
            .AddHttpClient(DefaultHttpClientName)
            .RemoveAllLoggers()
            .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
            });

        return new ReliableWebhooksBuilder(services, httpClientBuilder);
    }

    private static IWebhookRetryPolicy CreateRetryPolicy(IServiceProvider serviceProvider)
    {
        ReliableWebhooksOptions options = GetOptions(serviceProvider);
        IWebhookRetryJitterSource? jitterSource = serviceProvider.GetService<IWebhookRetryJitterSource>();
        return new DefaultWebhookRetryPolicy(options.Retry, jitterSource);
    }

    private static IWebhookDeliveryTransport CreateTransport(IServiceProvider serviceProvider)
    {
        ReliableWebhooksOptions options = GetOptions(serviceProvider);
        IHttpClientFactory httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        IWebhookHttpResponseClassifier classifier =
            serviceProvider.GetRequiredService<IWebhookHttpResponseClassifier>();
        IWebhookRequestSigner? signer = serviceProvider.GetService<IWebhookRequestSigner>();

        return new HttpClientFactoryWebhookDeliveryTransport(
            httpClientFactory,
            DefaultHttpClientName,
            classifier,
            options.Transport,
            signer);
    }

    private static IWebhookEnqueueService CreateEnqueueService(IServiceProvider serviceProvider)
    {
        ReliableWebhooksOptions options = GetOptions(serviceProvider);
        IWebhookDeliveryStore store = CreateScopedStore(serviceProvider);
        return new WebhookEnqueueService(store, options.Dispatcher.TimeProvider);
    }

    private static WebhookDispatcher CreateDispatcher(IServiceProvider serviceProvider)
    {
        ReliableWebhooksOptions options = GetOptions(serviceProvider);
        IWebhookDeliveryStore store = CreateScopedStore(serviceProvider);
        IWebhookDeliveryTransport transport = serviceProvider.GetRequiredService<IWebhookDeliveryTransport>();
        IWebhookRetryPolicy retryPolicy = serviceProvider.GetRequiredService<IWebhookRetryPolicy>();
        IWebhookDispatcherDelay? delay = serviceProvider.GetService<IWebhookDispatcherDelay>();
        ILogger logger = serviceProvider.GetService<ILogger<WebhookDispatcher>>()
            ?? NullLogger<WebhookDispatcher>.Instance;

        return new WebhookDispatcher(
            store,
            transport,
            retryPolicy,
            options.Dispatcher,
            delay,
            logger);
    }

    private static IWebhookDeliveryStore CreateScopedStore(IServiceProvider serviceProvider)
    {
        IServiceScopeFactory scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new ScopedWebhookDeliveryStore(scopeFactory);
    }

    private static ReliableWebhooksOptions GetOptions(IServiceProvider serviceProvider)
    {
        return serviceProvider.GetRequiredService<IOptions<ReliableWebhooksOptions>>().Value;
    }
}
