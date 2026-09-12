using System.Globalization;
using System.Net.Http.Headers;

namespace ReliableWebhooks;

/// <summary>
/// Sends exactly one outbound webhook attempt over HTTP and classifies its result.
/// </summary>
/// <remarks>
/// This type does not schedule retries, sleep between attempts, or own the supplied <see cref="HttpClient"/>.
/// Automatic redirects must be disabled on the supplied client so that each call represents exactly one POST
/// to the configured webhook destination and so that 3xx responses reach the response classifier directly.
/// When using <see cref="HttpClientHandler"/> or <see cref="SocketsHttpHandler"/>, set
/// <c>AllowAutoRedirect = false</c>. When using <c>IHttpClientFactory</c>, configure its primary handler with
/// automatic redirects disabled. This keeps the transport compatible with factory-created clients without
/// taking a dependency on the dependency-injection packages that provide that factory.
/// </remarks>
public sealed class WebhookHttpTransport : IWebhookDeliveryTransport
{
    private const int ReadBufferSize = 8 * 1024;

    private readonly HttpClient httpClient;
    private readonly IWebhookHttpResponseClassifier classifier;
    private readonly TimeSpan attemptTimeout;
    private readonly int maxResponseBodyBytes;
    private readonly IWebhookRequestSigner? signer;
    private readonly WebhookSigningOptions signingOptions;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebhookHttpTransport"/> class without request signing.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client used to send webhook requests. Its primary handler must have automatic redirects disabled.
    /// </param>
    /// <param name="classifier">An optional classifier that overrides the default HTTP status classification.</param>
    /// <param name="options">Optional transport settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The configured timeout is invalid or the response-body byte limit is negative.
    /// </exception>
    public WebhookHttpTransport(
        HttpClient httpClient,
        IWebhookHttpResponseClassifier? classifier = null,
        WebhookHttpTransportOptions? options = null)
        : this(httpClient, classifier, options, signer: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WebhookHttpTransport"/> class.
    /// </summary>
    /// <param name="httpClient">
    /// The HTTP client used to send webhook requests. Its primary handler must have automatic redirects disabled.
    /// </param>
    /// <param name="classifier">A classifier that overrides the default HTTP status classification, or <see langword="null"/>.</param>
    /// <param name="options">Transport settings, or <see langword="null"/> to use defaults.</param>
    /// <param name="signer">
    /// A request signer, or <see langword="null"/> to disable signing. When supplied, the transport adds webhook ID,
    /// event type, timestamp, and signature headers to each request.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="httpClient"/> is <see langword="null"/>, or signing options contain a null time provider.
    /// </exception>
    /// <exception cref="ArgumentException">Configured signing header names are empty or duplicated.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The configured timeout is invalid or the response-body byte limit is negative.
    /// </exception>
    public WebhookHttpTransport(
        HttpClient httpClient,
        IWebhookHttpResponseClassifier? classifier,
        WebhookHttpTransportOptions? options,
        IWebhookRequestSigner? signer)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        options ??= new WebhookHttpTransportOptions();

        if (options.AttemptTimeout != Timeout.InfiniteTimeSpan && options.AttemptTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.AttemptTimeout,
                "Attempt timeout must be positive or Timeout.InfiniteTimeSpan.");
        }

        if (options.MaxResponseBodyBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxResponseBodyBytes,
                "Maximum response body bytes cannot be negative.");
        }

        ArgumentNullException.ThrowIfNull(options.Signing);

        if (signer is not null)
        {
            ValidateSigningOptions(options.Signing);
        }

        this.httpClient = httpClient;
        this.classifier = classifier ?? new DefaultWebhookHttpResponseClassifier();
        attemptTimeout = options.AttemptTimeout;
        maxResponseBodyBytes = options.MaxResponseBodyBytes;
        this.signer = signer;
        signingOptions = options.Signing;
    }

    /// <summary>
    /// Sends one HTTP POST attempt for <paramref name="message"/>.
    /// </summary>
    /// <param name="message">The immutable webhook message to send.</param>
    /// <param name="cancellationToken">A token used to cancel the caller's operation.</param>
    /// <returns>The classified delivery result for the single HTTP attempt.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is canceled. Transport or <see cref="HttpClient"/> timeouts are
    /// returned as retryable timeout results instead.
    /// </exception>
    public async Task<WebhookDeliveryResult> SendAsync(
        WebhookMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] payload = message.Payload.ToArray();
        using CancellationTokenSource? timeoutSource = CreateTimeoutSource(cancellationToken);
        CancellationToken attemptToken = timeoutSource?.Token ?? cancellationToken;

        try
        {
            using HttpRequestMessage request = await CreateRequestAsync(
                message,
                payload,
                attemptToken).ConfigureAwait(false);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                attemptToken).ConfigureAwait(false);

            int statusCode = (int)response.StatusCode;
            WebhookDeliveryOutcome outcome = classifier.Classify(statusCode);

            if (!Enum.IsDefined(outcome))
            {
                throw new InvalidOperationException("The HTTP response classifier returned an undefined delivery outcome.");
            }

            (TimeSpan? Delay, DateTimeOffset? Date) retryAfter = ReadRetryAfter(response.Headers);
            (byte[] Body, bool Truncated) body = await CaptureResponseBodyAsync(
                response.Content,
                attemptToken,
                cancellationToken).ConfigureAwait(false);

            return WebhookDeliveryResult.FromHttpResponse(
                outcome,
                statusCode,
                body.Body,
                body.Truncated,
                retryAfter.Delay,
                retryAfter.Date);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WebhookDeliveryResult.TimeoutFailure();
        }
        catch (HttpRequestException)
        {
            return WebhookDeliveryResult.NetworkFailure();
        }
    }

    private static void ValidateSigningOptions(WebhookSigningOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WebhookIdHeaderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.EventTypeHeaderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TimestampHeaderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SignatureHeaderName);
        ArgumentNullException.ThrowIfNull(options.TimeProvider);

        string[] headerNames =
        [
            options.WebhookIdHeaderName,
            options.EventTypeHeaderName,
            options.TimestampHeaderName,
            options.SignatureHeaderName,
        ];

        if (headerNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headerNames.Length)
        {
            throw new ArgumentException("Signing header names must be unique.", nameof(options));
        }
    }

    private async ValueTask<HttpRequestMessage> CreateRequestAsync(
        WebhookMessage message,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        HttpRequestMessage request = new(HttpMethod.Post, message.Destination);

        try
        {
            ByteArrayContent content = new(payload);
            _ = content.Headers.TryAddWithoutValidation("Content-Type", message.ContentType);
            request.Content = content;

            foreach ((string name, string value) in message.Headers)
            {
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!request.Headers.TryAddWithoutValidation(name, value)
                    && !content.Headers.TryAddWithoutValidation(name, value))
                {
                    throw new InvalidOperationException($"The custom header '{name}' could not be applied to the HTTP request.");
                }
            }

            if (signer is not null)
            {
                DateTimeOffset timestamp = signingOptions.TimeProvider.GetUtcNow().ToUniversalTime();
                string signature = await signer
                    .SignAsync(message, payload, timestamp, cancellationToken)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(signature))
                {
                    throw new InvalidOperationException("The configured webhook signer returned an empty signature.");
                }

                string timestampValue = timestamp
                    .ToUnixTimeSeconds()
                    .ToString(CultureInfo.InvariantCulture);

                ApplyGeneratedHeader(request, content, signingOptions.WebhookIdHeaderName, message.Id);
                ApplyGeneratedHeader(request, content, signingOptions.EventTypeHeaderName, message.EventType);
                ApplyGeneratedHeader(request, content, signingOptions.TimestampHeaderName, timestampValue);
                ApplyGeneratedHeader(request, content, signingOptions.SignatureHeaderName, signature);
            }

            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private static void ApplyGeneratedHeader(
        HttpRequestMessage request,
        HttpContent content,
        string name,
        string value)
    {
        _ = request.Headers.Remove(name);
        _ = content.Headers.Remove(name);

        if (!request.Headers.TryAddWithoutValidation(name, value))
        {
            throw new InvalidOperationException($"The generated webhook header '{name}' could not be applied to the HTTP request.");
        }
    }

    private static (TimeSpan? Delay, DateTimeOffset? Date) ReadRetryAfter(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Retry-After", out IEnumerable<string>? values))
        {
            return (null, null);
        }

        foreach (string value in values)
        {
            if (RetryConditionHeaderValue.TryParse(value, out RetryConditionHeaderValue? parsed)
                && parsed is not null)
            {
                return (parsed.Delta, parsed.Date);
            }
        }

        return (null, null);
    }

    private CancellationTokenSource? CreateTimeoutSource(CancellationToken cancellationToken)
    {
        if (attemptTimeout == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(attemptTimeout);
        return source;
    }

    private async Task<(byte[] Body, bool Truncated)> CaptureResponseBodyAsync(
        HttpContent content,
        CancellationToken attemptToken,
        CancellationToken callerToken)
    {
        try
        {
            return await ReadBoundedResponseBodyAsync(content, attemptToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ([], true);
        }
        catch (HttpRequestException)
        {
            return ([], true);
        }
        catch (IOException)
        {
            return ([], true);
        }
    }

    private async Task<(byte[] Body, bool Truncated)> ReadBoundedResponseBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream captured = new(Math.Min(maxResponseBodyBytes, ReadBufferSize));
        byte[] buffer = new byte[ReadBufferSize];
        int remaining = maxResponseBodyBytes;

        while (remaining > 0)
        {
            int requested = Math.Min(buffer.Length, remaining);
            int read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return (captured.ToArray(), false);
            }

            captured.Write(buffer, 0, read);
            remaining -= read;
        }

        int probe = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        return (captured.ToArray(), probe > 0);
    }
}
