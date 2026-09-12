namespace ReliableWebhooks;

internal sealed class HttpClientFactoryWebhookDeliveryTransport : IWebhookDeliveryTransport
{
    private readonly IHttpClientFactory httpClientFactory;
    private readonly string clientName;
    private readonly IWebhookHttpResponseClassifier classifier;
    private readonly WebhookHttpTransportOptions options;
    private readonly IWebhookRequestSigner? signer;

    internal HttpClientFactoryWebhookDeliveryTransport(
        IHttpClientFactory httpClientFactory,
        string clientName,
        IWebhookHttpResponseClassifier classifier,
        WebhookHttpTransportOptions options,
        IWebhookRequestSigner? signer)
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
    }

    public async Task<WebhookDeliveryResult> SendAsync(
        WebhookMessage message,
        CancellationToken cancellationToken = default)
    {
        using HttpClient client = httpClientFactory.CreateClient(clientName);
        WebhookHttpTransport transport = new(client, classifier, options, signer);
        return await transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
