using Microsoft.Extensions.Options;

namespace ReliableWebhooks;

internal sealed class ReliableWebhooksOptionsValidator : IValidateOptions<ReliableWebhooksOptions>
{
    public ValidateOptionsResult Validate(string? name, ReliableWebhooksOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        ValidateDispatcher(options.Dispatcher, failures);
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
}
