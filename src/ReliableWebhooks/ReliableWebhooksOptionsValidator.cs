using Microsoft.Extensions.Options;

namespace ReliableWebhooks;

internal sealed class ReliableWebhooksOptionsValidator : IValidateOptions<ReliableWebhooksOptions>
{
    public ValidateOptionsResult Validate(string? name, ReliableWebhooksOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        ValidateDispatcher(options.Dispatcher, failures);
        ValidateMessageLimits(options.MessageLimits, failures);
        ValidateRetry(options.Retry, failures);
        ValidateTransport(options.Transport, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateDispatcher(
        WebhookDispatcherOptions? options,
        List<string> failures)
    {
        if (options is null)
        {
            failures.Add("ReliableWebhooksOptions.Dispatcher must not be null.");
            return;
        }

        if (options.MaxConcurrency < 1)
        {
            failures.Add("Dispatcher.MaxConcurrency must be greater than zero.");
        }

        if (options.LeaseDuration <= TimeSpan.Zero)
        {
            failures.Add("Dispatcher.LeaseDuration must be greater than zero.");
        }

        if (options.PollInterval <= TimeSpan.Zero)
        {
            failures.Add("Dispatcher.PollInterval must be greater than zero.");
        }

        if (options.ShutdownGracePeriod < TimeSpan.Zero)
        {
            failures.Add("Dispatcher.ShutdownGracePeriod cannot be negative.");
        }

        if (options.TimeProvider is null)
        {
            failures.Add("Dispatcher.TimeProvider must not be null.");
        }

        ValidateMetrics(options.Metrics, "Dispatcher.Metrics", failures);
    }

    private static void ValidateMetrics(
        WebhookMetricsOptions? options,
        string path,
        List<string> failures)
    {
        if (options is null)
        {
            failures.Add($"{path} must not be null.");
            return;
        }

        if (options.EventTypeTagAllowList is null)
        {
            failures.Add($"{path}.EventTypeTagAllowList must not be null.");
            return;
        }

        foreach (string eventType in options.EventTypeTagAllowList)
        {
            if (string.IsNullOrWhiteSpace(eventType))
            {
                failures.Add($"{path}.EventTypeTagAllowList must not contain empty or whitespace values.");
                break;
            }

            if (ContainsControlCharacter(eventType))
            {
                failures.Add($"{path}.EventTypeTagAllowList must not contain control characters.");
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(options.UnknownEventTypeTagValue))
        {
            failures.Add($"{path}.UnknownEventTypeTagValue must not be empty or whitespace.");
        }
        else if (ContainsControlCharacter(options.UnknownEventTypeTagValue))
        {
            failures.Add($"{path}.UnknownEventTypeTagValue must not contain control characters.");
        }
    }

    private static void ValidateMessageLimits(
        WebhookMessageLimits? options,
        List<string> failures)
    {
        if (options is null)
        {
            failures.Add("ReliableWebhooksOptions.MessageLimits must not be null.");
            return;
        }

        if (options.MaxPayloadBytes < 0)
        {
            failures.Add("MessageLimits.MaxPayloadBytes cannot be negative.");
        }

        if (options.MaxCustomHeaders < 0)
        {
            failures.Add("MessageLimits.MaxCustomHeaders cannot be negative.");
        }

        if (options.MaxCustomHeaderBytes < 0)
        {
            failures.Add("MessageLimits.MaxCustomHeaderBytes cannot be negative.");
        }

        if (options.MaxIdCharacters < 1)
        {
            failures.Add("MessageLimits.MaxIdCharacters must be greater than zero.");
        }

        if (options.MaxEventTypeCharacters < 1)
        {
            failures.Add("MessageLimits.MaxEventTypeCharacters must be greater than zero.");
        }

        if (options.MaxContentTypeCharacters < 1)
        {
            failures.Add("MessageLimits.MaxContentTypeCharacters must be greater than zero.");
        }

        if (options.MaxDestinationUriCharacters < 1)
        {
            failures.Add("MessageLimits.MaxDestinationUriCharacters must be greater than zero.");
        }
    }

    private static void ValidateRetry(
        WebhookRetryPolicyOptions? options,
        List<string> failures)
    {
        if (options is null)
        {
            failures.Add("ReliableWebhooksOptions.Retry must not be null.");
            return;
        }

        if (options.MaxAttempts < 1)
        {
            failures.Add("Retry.MaxAttempts must be greater than zero.");
        }

        if (options.BaseDelay <= TimeSpan.Zero)
        {
            failures.Add("Retry.BaseDelay must be greater than zero.");
        }

        if (options.MaxDelay <= TimeSpan.Zero)
        {
            failures.Add("Retry.MaxDelay must be greater than zero.");
        }
        else if (options.MaxDelay < options.BaseDelay)
        {
            failures.Add("Retry.MaxDelay cannot be shorter than Retry.BaseDelay.");
        }

        if (!double.IsFinite(options.JitterFactor)
            || options.JitterFactor < 0
            || options.JitterFactor > 1)
        {
            failures.Add("Retry.JitterFactor must be a finite value from zero through one.");
        }
    }

    private static void ValidateTransport(
        WebhookHttpTransportOptions? options,
        List<string> failures)
    {
        if (options is null)
        {
            failures.Add("ReliableWebhooksOptions.Transport must not be null.");
            return;
        }

        if (options.AttemptTimeout != Timeout.InfiniteTimeSpan
            && options.AttemptTimeout <= TimeSpan.Zero)
        {
            failures.Add("Transport.AttemptTimeout must be positive or Timeout.InfiniteTimeSpan.");
        }

        if (options.MaxResponseBodyBytes < 0)
        {
            failures.Add("Transport.MaxResponseBodyBytes cannot be negative.");
        }

        ValidateSigning(options.Signing, failures);
    }

    private static void ValidateSigning(
        WebhookSigningOptions? options,
        List<string> failures)
    {
        if (options is null)
        {
            failures.Add("Transport.Signing must not be null.");
            return;
        }

        string[] headerNames =
        [
            options.WebhookIdHeaderName,
            options.EventTypeHeaderName,
            options.TimestampHeaderName,
            options.SignatureHeaderName,
        ];

        if (headerNames.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add("Signing header names must not be empty or whitespace.");
        }
        else if (headerNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != headerNames.Length)
        {
            failures.Add("Signing header names must be unique, using case-insensitive comparison.");
        }

        if (options.TimeProvider is null)
        {
            failures.Add("Transport.Signing.TimeProvider must not be null.");
        }
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (char character in value)
        {
            if (character <= '\u001f' || character == '\u007f')
            {
                return true;
            }
        }

        return false;
    }
}
