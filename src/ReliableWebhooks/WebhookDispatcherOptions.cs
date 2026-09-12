namespace ReliableWebhooks;

/// <summary>
/// Configures concurrent webhook dispatching, leasing, polling, and graceful shutdown.
/// </summary>
public sealed class WebhookDispatcherOptions
{
    /// <summary>
    /// Gets or sets the maximum number of deliveries processed concurrently.
    /// </summary>
    public int MaxConcurrency
    {
        get;
        set;
    } = 4;

    /// <summary>
    /// Gets or sets how long an acquired delivery lease remains valid.
    /// </summary>
    public TimeSpan LeaseDuration
    {
        get;
        set;
    } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the delay used when no due deliveries are available.
    /// </summary>
    public TimeSpan PollInterval
    {
        get;
        set;
    } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets how long shutdown waits for in-flight deliveries before canceling them.
    /// </summary>
    public TimeSpan ShutdownGracePeriod
    {
        get;
        set;
    } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the time provider used for claims and persisted state transitions.
    /// </summary>
    public TimeProvider TimeProvider
    {
        get;
        set;
    } = TimeProvider.System;
}
