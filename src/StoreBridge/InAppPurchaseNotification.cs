namespace StoreBridge;

/// <summary>
/// Parsed and normalized representation of a server-to-server in-app purchase notification from Apple or Google.
/// </summary>
public sealed class InAppPurchaseNotification
{
    /// <summary>The store that sent this notification.</summary>
    public Store Store { get; init; }

    /// <summary>Normalized event type.</summary>
    public InAppPurchaseEventType EventType { get; init; }

    /// <summary>Platform-specific raw notification type string (e.g. "REFUND", "1").</summary>
    public string RawEventType { get; init; } = string.Empty;

    /// <summary>
    /// Unique identifier for this notification. Use it to deduplicate retries:
    /// Apple sets <c>notificationUUID</c>; Google's Pub/Sub <c>messageId</c> is used for Android.
    /// </summary>
    public string NotificationId { get; init; } = string.Empty;

    /// <summary>Transaction ID (Apple) or purchase token (Google).</summary>
    public string PurchaseId { get; init; } = string.Empty;

    /// <summary>The product identifier of the purchased item.</summary>
    public string ProductId { get; init; } = string.Empty;

    /// <summary>When the event occurred, if the platform includes a timestamp.</summary>
    public DateTimeOffset? EventAt { get; init; }

    /// <summary>Whether this notification originated from a sandbox or test environment.</summary>
    public bool IsSandbox { get; init; }
}
