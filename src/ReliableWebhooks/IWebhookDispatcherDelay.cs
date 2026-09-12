namespace ReliableWebhooks;

/// <summary>
/// Provides the delay used between dispatcher polling cycles.
/// </summary>
/// <remarks>
/// Applications normally use the built-in implementation. This abstraction allows deterministic tests
/// and specialized schedulers to control polling without relying on real-time sleeps.
/// </remarks>
public interface IWebhookDispatcherDelay
{
    /// <summary>
    /// Waits for the requested polling interval.
    /// </summary>
    /// <param name="delay">The polling delay.</param>
    /// <param name="cancellationToken">A token used to cancel the wait.</param>
    /// <returns>A task that completes when the delay elapses or cancellation is requested.</returns>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}
