using StoreBridge.Android.Internal;

namespace StoreBridge.Android;

/// <summary>
/// Parses Google Play Real-time Developer Notifications delivered via Pub/Sub.
/// </summary>
public sealed class AndroidWebhookParser : IWebhookParser
{
    /// <inheritdoc />
    public Store Store => Store.Android;

    /// <summary>
    /// Parses the Pub/Sub push message body from Google Play.
    /// </summary>
    /// <param name="rawBody">
    /// The raw JSON body from the Pub/Sub HTTP push endpoint:
    /// <c>{ "message": { "data": "&lt;base64&gt;", ... }, "subscription": "..." }</c>
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<SubscriptionNotification> ParseAsync(string rawBody, CancellationToken cancellationToken = default)
    {
        var (notification, messageId) = AndroidPubSubReader.Parse(rawBody);

        // testNotification is a wiring check sent from Play Console; it has no subscription payload
        if (notification.TestNotification != null && notification.SubscriptionNotification == null)
        {
            return Task.FromResult(new SubscriptionNotification
            {
                Store = Store,
                EventType = NotificationEventType.Test,
                RawEventType = "TEST",
                NotificationId = messageId,
                Status = SubscriptionStatus.Unknown,
                EventAt = notification.EventTimeMillis.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(notification.EventTimeMillis.Value)
                    : null,
                IsSandbox = false
            });
        }

        var sub = notification.SubscriptionNotification;
        if (sub == null)
            throw new FormatException("No subscriptionNotification found in payload. One-time product notifications are not handled by this parser.");

        var result = new SubscriptionNotification
        {
            Store = Store,
            EventType = MapEventType(sub.NotificationType),
            RawEventType = sub.NotificationType.ToString(),
            NotificationId = messageId,
            SubscriptionId = sub.PurchaseToken ?? string.Empty,
            ProductId = sub.SubscriptionId ?? string.Empty,
            Status = MapStatus(sub.NotificationType),
            EventAt = notification.EventTimeMillis.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(notification.EventTimeMillis.Value)
                : null,
            IsSandbox = false
        };

        return Task.FromResult(result);
    }

    // https://developer.android.com/google/play/billing/rtdn-reference#sub
    private static NotificationEventType MapEventType(int notificationType) => notificationType switch
    {
        1 => NotificationEventType.Renewed,           // SUBSCRIPTION_RECOVERED
        2 => NotificationEventType.Renewed,           // SUBSCRIPTION_RENEWED
        3 => NotificationEventType.AutoRenewDisabled, // SUBSCRIPTION_CANCELED
        4 => NotificationEventType.Created,           // SUBSCRIPTION_PURCHASED
        5 => NotificationEventType.InBillingRetry,    // SUBSCRIPTION_ON_HOLD
        6 => NotificationEventType.GracePeriod,       // SUBSCRIPTION_IN_GRACE_PERIOD
        7 => NotificationEventType.Renewed,           // SUBSCRIPTION_RESTARTED
        8 => NotificationEventType.Other,             // SUBSCRIPTION_PRICE_CHANGE_CONFIRMED
        9 => NotificationEventType.Other,             // SUBSCRIPTION_DEFERRED
        10 => NotificationEventType.Other,            // SUBSCRIPTION_PAUSED
        11 => NotificationEventType.Other,            // SUBSCRIPTION_PAUSE_SCHEDULE_CHANGED
        12 => NotificationEventType.Refunded,         // SUBSCRIPTION_REVOKED
        13 => NotificationEventType.Expired,          // SUBSCRIPTION_EXPIRED
        _ => NotificationEventType.Other
    };

    private static SubscriptionStatus MapStatus(int notificationType) => notificationType switch
    {
        1 or 2 or 4 or 7 => SubscriptionStatus.Active,
        3 => SubscriptionStatus.Cancelled,
        5 => SubscriptionStatus.InBillingRetry,
        6 => SubscriptionStatus.InGracePeriod,
        12 => SubscriptionStatus.Revoked,
        13 => SubscriptionStatus.Expired,
        _ => SubscriptionStatus.Unknown
    };
}
