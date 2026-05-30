namespace StoreBridge;

/// <summary>
/// Parsed and normalized representation of a server-to-server notification from Apple or Google.
/// </summary>
public sealed class SubscriptionNotification
{
    /// <summary>The store that sent this notification.</summary>
    public Store Store { get; init; }

    /// <summary>Normalized event type.</summary>
    public NotificationEventType EventType { get; init; }

    /// <summary>Platform-specific raw notification type string (e.g. "DID_RENEW", "SUBSCRIPTION_RENEWED").</summary>
    public string RawEventType { get; init; } = string.Empty;

    /// <summary>
    /// Unique identifier for this notification. Use it to deduplicate retries:
    /// Apple sets <c>notificationUUID</c>; Google's Pub/Sub <c>messageId</c> is used for Android.
    /// </summary>
    public string NotificationId { get; init; } = string.Empty;

    /// <summary>Original transaction ID (Apple) or purchase token (Google).</summary>
    public string SubscriptionId { get; init; } = string.Empty;

    /// <summary>The subscription product identifier.</summary>
    public string ProductId { get; init; } = string.Empty;

    /// <summary>Subscription lifecycle status at the time of the notification.</summary>
    public SubscriptionStatus Status { get; init; }

    /// <summary>When the current subscription period expires, if known.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When the event occurred, if the platform includes a timestamp.</summary>
    public DateTimeOffset? EventAt { get; init; }

    /// <summary>Whether this notification originated from a sandbox or test environment.</summary>
    public bool IsSandbox { get; init; }
}
