using Microsoft.Extensions.Logging;

namespace ReliableWebhooks;

internal static partial class ReliableWebhooksLog
{
    [LoggerMessage(1001, LogLevel.Information, "Webhook {WebhookId} with event type {EventType} was enqueued.")]
    internal static partial void Enqueued(ILogger logger, string webhookId, string eventType);

    [LoggerMessage(1002, LogLevel.Debug, "Webhook {WebhookId} with event type {EventType} was claimed for attempt {Attempt}.")]
    internal static partial void Claimed(ILogger logger, string webhookId, string eventType, int attempt);

    [LoggerMessage(1003, LogLevel.Debug, "Webhook {WebhookId} with event type {EventType} started attempt {Attempt}.")]
    internal static partial void AttemptStarted(ILogger logger, string webhookId, string eventType, int attempt);

    [LoggerMessage(1004, LogLevel.Information, "Webhook {WebhookId} with event type {EventType} succeeded on attempt {Attempt}.")]
    internal static partial void AttemptSucceeded(ILogger logger, string webhookId, string eventType, int attempt);

    [LoggerMessage(1005, LogLevel.Warning, "Webhook {WebhookId} with event type {EventType} scheduled retry after attempt {Attempt} at {NextAttemptAt}.")]
    internal static partial void RetryScheduled(
        ILogger logger,
        string webhookId,
        string eventType,
        int attempt,
        DateTimeOffset nextAttemptAt);

    [LoggerMessage(1006, LogLevel.Warning, "Webhook {WebhookId} with event type {EventType} failed permanently on attempt {Attempt}.")]
    internal static partial void PermanentlyFailed(ILogger logger, string webhookId, string eventType, int attempt);

    [LoggerMessage(1007, LogLevel.Error, "Webhook {WebhookId} with event type {EventType} was dead-lettered on attempt {Attempt}.")]
    internal static partial void DeadLettered(ILogger logger, string webhookId, string eventType, int attempt);

    [LoggerMessage(1008, LogLevel.Debug, "Webhook {WebhookId} with event type {EventType} was canceled during attempt {Attempt}; the lease remains recoverable.")]
    internal static partial void AttemptCanceled(ILogger logger, string webhookId, string eventType, int attempt);

    [LoggerMessage(1009, LogLevel.Debug, "Webhook {WebhookId} with event type {EventType} lost lease ownership during attempt {Attempt}.")]
    internal static partial void LeaseOwnershipLost(ILogger logger, string webhookId, string eventType, int attempt);

    [LoggerMessage(1010, LogLevel.Error, "Webhook {WebhookId} with event type {EventType} failed unexpectedly on attempt {Attempt} with exception type {ExceptionType}.")]
    internal static partial void AttemptFailedUnexpectedly(
        ILogger logger,
        string webhookId,
        string eventType,
        int attempt,
        string exceptionType);
}
