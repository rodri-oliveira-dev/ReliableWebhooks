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
public sealed class WebhookHttpTransport
{
    private const int ReadBufferSize = 8 * 1024;

    private readonly HttpClient httpClient;
    private readonly IWebhookHttpResponseClassifier classifier;
    private readonly TimeSpan attemptTimeout;
    private readonly int maxResponseBodyBytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebhookHttpTransport"/> class.
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

        this.httpClient = httpClient;
        this.classifier = classifier ?? new DefaultWebhookHttpResponseClassifier();
        attemptTimeout = options.AttemptTimeout;
        maxResponseBodyBytes = options.MaxResponseBodyBytes;
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

        using HttpRequestMessage request = CreateRequest(message);
        using CancellationTokenSource? timeoutSource = CreateTimeoutSource(cancellationToken);
        CancellationToken attemptToken = timeoutSource?.Token ?? cancellationToken;

        try
        {
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

    private static HttpRequestMessage CreateRequest(WebhookMessage message)
    {
        HttpRequestMessage request = new(HttpMethod.Post, message.Destination);
        ByteArrayContent content = new(message.Payload.ToArray());

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
                request.Dispose();
                throw new InvalidOperationException($"The custom header '{name}' could not be applied to the HTTP request.");
            }
        }

        return request;
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
