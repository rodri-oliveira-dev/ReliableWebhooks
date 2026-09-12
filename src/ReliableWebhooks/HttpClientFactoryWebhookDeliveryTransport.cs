namespace ReliableWebhooks;

internal sealed class HttpClientFactoryWebhookDeliveryTransport : IWebhookDeliveryTransport
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly string clientName;
    private readonly IWebhookHttpResponseClassifier classifier;
    private readonly WebhookHttpTransportOptions options;
    private readonly IWebhookRequestSigner? signer;
    private readonly IWebhookRequestHeaderProvider? headerProvider;

    internal HttpClientFactoryWebhookDeliveryTransport(
        IHttpClientFactory httpClientFactory,
        string clientName,
        IWebhookHttpResponseClassifier classifier,
        WebhookHttpTransportOptions options,
        IWebhookRequestSigner? signer,
        IWebhookRequestHeaderProvider? headerProvider)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(options);

        this.httpClientFactory = httpClientFactory;
        this.clientName = clientName;
        this.classifier = classifier;
        this.options = options;
        this.signer = signer;
        this.headerProvider = headerProvider;
    }

    public async Task<WebhookDeliveryResult> SendAsync(
        WebhookMessage message,
        CancellationToken cancellationToken = default)
    {
        using HttpClient client = httpClientFactory.CreateClient(clientName);
        WebhookHttpTransport transport = new(client, classifier, options, signer, headerProvider);
        return await transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
