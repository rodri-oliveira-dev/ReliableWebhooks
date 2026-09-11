namespace ReliableWebhooks;

/// <summary>
/// Represents the transport-neutral result of one webhook delivery attempt.
/// </summary>
public sealed class WebhookDeliveryResult
{
    private readonly byte[] responseBody;

    private WebhookDeliveryResult(
        WebhookDeliveryOutcome outcome,
        int? statusCode,
        WebhookTransportFailureKind failureKind,
        ReadOnlyMemory<byte> responseBody,
        bool responseBodyTruncated,
        TimeSpan? retryAfterDelay,
        DateTimeOffset? retryAfterDate)
    {
        Outcome = outcome;
        StatusCode = statusCode;
        FailureKind = failureKind;
        this.responseBody = responseBody.ToArray();
        ResponseBodyTruncated = responseBodyTruncated;
        RetryAfterDelay = retryAfterDelay;
        RetryAfterDate = retryAfterDate;
    }

    /// <summary>
    /// Gets the classified outcome for this attempt.
    /// </summary>
    public WebhookDeliveryOutcome Outcome
    {
        get;
    }

    /// <summary>
    /// Gets the numeric HTTP status code when a usable response was received.
    /// </summary>
    public int? StatusCode
    {
        get;
    }

    /// <summary>
    /// Gets the transport-level failure kind when no usable HTTP response was received.
    /// </summary>
    public WebhookTransportFailureKind FailureKind
    {
        get;
    }

    /// <summary>
    /// Gets a defensive copy of the bounded response-body bytes captured for diagnostics.
    /// </summary>
    public ReadOnlyMemory<byte> ResponseBody => responseBody.ToArray();

    /// <summary>
    /// Gets a value indicating whether the response body was not captured completely.
    /// </summary>
    /// <remarks>
    /// This value is <see langword="true"/> when the configured byte limit was exceeded or when body
    /// capture could not finish after response headers had already been received. HTTP status
    /// classification remains authoritative in that case.
    /// </remarks>
    public bool ResponseBodyTruncated
    {
        get;
    }

    /// <summary>
    /// Gets the delta-form <c>Retry-After</c> value when present on the HTTP response.
    /// </summary>
    public TimeSpan? RetryAfterDelay
    {
        get;
    }

    /// <summary>
    /// Gets the date-form <c>Retry-After</c> value when present on the HTTP response.
    /// </summary>
    public DateTimeOffset? RetryAfterDate
    {
        get;
    }

    internal static WebhookDeliveryResult FromHttpResponse(
        WebhookDeliveryOutcome outcome,
        int statusCode,
        ReadOnlyMemory<byte> responseBody,
        bool responseBodyTruncated,
        TimeSpan? retryAfterDelay,
        DateTimeOffset? retryAfterDate)
    {
        return new WebhookDeliveryResult(
            outcome,
            statusCode,
            WebhookTransportFailureKind.None,
            responseBody,
            responseBodyTruncated,
            retryAfterDelay,
            retryAfterDate);
    }

    internal static WebhookDeliveryResult NetworkFailure()
    {
        return new WebhookDeliveryResult(
            WebhookDeliveryOutcome.RetryableFailure,
            null,
            WebhookTransportFailureKind.Network,
            ReadOnlyMemory<byte>.Empty,
            false,
            null,
            null);
    }

    internal static WebhookDeliveryResult TimeoutFailure()
    {
        return new WebhookDeliveryResult(
            WebhookDeliveryOutcome.RetryableFailure,
            null,
            WebhookTransportFailureKind.Timeout,
            ReadOnlyMemory<byte>.Empty,
            false,
            null,
            null);
    }
}
