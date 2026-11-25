using StoreBridge.Android.Internal;

namespace StoreBridge.Android;

/// <summary>
/// Parses Google Play Real-time Developer Notifications for subscriptions delivered via Pub/Sub:
/// <c>subscriptionNotification</c>, <c>voidedPurchaseNotification</c> for subscriptions, and <c>testNotification</c>.
/// </summary>
/// <remarks>
/// RTDNs only signal that something changed. Call <see cref="AndroidSubscriptionVerifier"/> afterwards for the
/// authoritative state (and the product ID, which recent notifications no longer carry).
/// </remarks>
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
    /// <exception cref="FormatException">
    /// The payload is malformed, or it is a one-time product / refund-review notification — hand those to
    /// <see cref="AndroidInAppPurchaseWebhookParser"/>.
    /// </exception>
    public Task<SubscriptionNotification> ParseAsync(string rawBody, CancellationToken cancellationToken = default)
    {
        var (notification, messageId) = AndroidPubSubReader.Parse(rawBody);
        var eventAt = notification.EventTimeMillis.HasValue
            ? DateTimeOffset.FromUnixTimeMilliseconds(notification.EventTimeMillis.Value)
            : (DateTimeOffset?)null;

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
                EventAt = eventAt,
                IsSandbox = false
            });
        }

        // A refunded/charged-back subscription arrives as a voided purchase, not as a subscription notification
        if (notification.VoidedPurchaseNotification is { ProductType: VoidedProductType.Subscription } voided)
        {
            return Task.FromResult(new SubscriptionNotification
            {
                Store = Store,
                EventType = NotificationEventType.Refunded,
                RawEventType = $"VOIDED:{voided.RefundType}:{voided.ProductType}",
                NotificationId = messageId,
                SubscriptionId = voided.PurchaseToken ?? string.Empty,
                Status = SubscriptionStatus.Revoked,
                EventAt = eventAt,
                IsSandbox = false
            });
        }

        var sub = notification.SubscriptionNotification
            ?? throw new FormatException(
                "No subscriptionNotification found in payload. One-time product, one-time voided purchase and " +
                "refund-review notifications are handled by AndroidInAppPurchaseWebhookParser.");

        var result = new SubscriptionNotification
        {
            Store = Store,
            EventType = MapEventType(sub.NotificationType),
            RawEventType = sub.NotificationType.ToString(),
            NotificationId = messageId,
            SubscriptionId = sub.PurchaseToken ?? string.Empty,
            // Legacy field — may be empty; use the verifier for the authoritative product ID
            ProductId = sub.SubscriptionId ?? string.Empty,
            Status = MapStatus(sub.NotificationType),
            EventAt = eventAt,
            // RTDNs carry no test flag; use the verifier's IsSandbox (testPurchase) instead
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
        7 => NotificationEventType.AutoRenewEnabled,  // SUBSCRIPTION_RESTARTED (user restored before expiry)
        8 => NotificationEventType.Other,             // SUBSCRIPTION_PRICE_CHANGE_CONFIRMED (deprecated)
        9 => NotificationEventType.Other,             // SUBSCRIPTION_DEFERRED
        10 => NotificationEventType.Other,            // SUBSCRIPTION_PAUSED
        11 => NotificationEventType.Other,            // SUBSCRIPTION_PAUSE_SCHEDULE_CHANGED
        12 => NotificationEventType.Refunded,         // SUBSCRIPTION_REVOKED
        13 => NotificationEventType.Expired,          // SUBSCRIPTION_EXPIRED
        17 => NotificationEventType.Other,            // SUBSCRIPTION_ITEMS_CHANGED
        18 => NotificationEventType.AutoRenewDisabled, // SUBSCRIPTION_CANCELLATION_SCHEDULED (installments)
        19 => NotificationEventType.Other,            // SUBSCRIPTION_PRICE_CHANGE_UPDATED
        20 => NotificationEventType.Other,            // SUBSCRIPTION_PENDING_PURCHASE_CANCELED
        21 => NotificationEventType.Other,            // SUBSCRIPTION_PLAN_TRANSITION_UPDATED
        22 => NotificationEventType.Other,            // SUBSCRIPTION_PRICE_STEP_UP_CONSENT_UPDATED
        _ => NotificationEventType.Other
    };

    private static SubscriptionStatus MapStatus(int notificationType) => notificationType switch
    {
        1 or 2 or 4 or 7 => SubscriptionStatus.Active,
        3 or 18 => SubscriptionStatus.Cancelled,
        5 => SubscriptionStatus.InBillingRetry,
        6 => SubscriptionStatus.InGracePeriod,
        12 => SubscriptionStatus.Revoked,
        13 => SubscriptionStatus.Expired,
        _ => SubscriptionStatus.Unknown
    };
}
