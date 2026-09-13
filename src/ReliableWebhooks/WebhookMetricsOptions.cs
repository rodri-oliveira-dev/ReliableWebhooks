namespace ReliableWebhooks;

/// <summary>
/// Configures built-in metric dimensions.
/// </summary>
public sealed class WebhookMetricsOptions
{
    /// <summary>
    /// Gets or sets the event types that may be emitted as the <c>webhook.event_type</c> metric tag.
    /// </summary>
    /// <remarks>
    /// The default set is empty, which omits the event-type metric tag. When at least one value is configured,
    /// matching event types are emitted as-is and all other event types use <see cref="UnknownEventTypeTagValue"/>.
    /// </remarks>
    public IReadOnlySet<string> EventTypeTagAllowList
    {
        get;
        set;
    } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the bounded fallback tag value used for event types not present in <see cref="EventTypeTagAllowList"/>.
    /// </summary>
    public string UnknownEventTypeTagValue
    {
        get;
        set;
    } = "other";
}
