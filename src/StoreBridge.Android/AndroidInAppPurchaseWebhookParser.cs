using StoreBridge.Android.Internal;

namespace StoreBridge.Android;

/// <summary>
/// Parses Google Play Real-time Developer Notifications for one-time in-app purchase events
/// delivered via Pub/Sub (ONE_TIME_PRODUCT_PURCHASED, ONE_TIME_PRODUCT_CANCELED).
/// </summary>
public sealed class AndroidInAppPurchaseWebhookParser : IInAppPurchaseWebhookParser
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
    public Task<InAppPurchaseNotification> ParseAsync(string rawBody, CancellationToken cancellationToken = default)
    {
        var (notification, messageId) = AndroidPubSubReader.Parse(rawBody);

        // testNotification is a wiring check sent from Play Console; it has no purchase payload
        if (notification.TestNotification != null && notification.OneTimeProductNotification == null
            && notification.VoidedPurchaseNotification == null)
        {
            return Task.FromResult(new InAppPurchaseNotification
            {
                Store = Store,
                EventType = InAppPurchaseEventType.Test,
                RawEventType = "TEST",
                NotificationId = messageId,
                EventAt = notification.EventTimeMillis.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(notification.EventTimeMillis.Value)
                    : null,
                IsSandbox = false
            });
        }

        // voidedPurchaseNotification covers refunds/chargebacks for both subscriptions and one-time purchases
        if (notification.VoidedPurchaseNotification is { } voided)
        {
            return Task.FromResult(new InAppPurchaseNotification
            {
                Store = Store,
                EventType = InAppPurchaseEventType.Refunded,
                RawEventType = $"VOIDED:{voided.RefundType}:{voided.ProductType}",
                NotificationId = messageId,
                PurchaseId = voided.PurchaseToken ?? string.Empty,
                ProductId = voided.OrderId ?? string.Empty,
                EventAt = notification.EventTimeMillis.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(notification.EventTimeMillis.Value)
                    : null,
                IsSandbox = false
            });
        }

        var product = notification.OneTimeProductNotification;
        if (product == null)
            throw new FormatException("No oneTimeProductNotification found in payload. Subscription notifications are not handled by this parser.");

        var result = new InAppPurchaseNotification
        {
            Store = Store,
            EventType = MapEventType(product.NotificationType),
            RawEventType = product.NotificationType.ToString(),
            NotificationId = messageId,
            PurchaseId = product.PurchaseToken ?? string.Empty,
            ProductId = product.Sku ?? string.Empty,
            EventAt = notification.EventTimeMillis.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(notification.EventTimeMillis.Value)
                : null,
            IsSandbox = false
        };

        return Task.FromResult(result);
    }

    // https://developer.android.com/google/play/billing/rtdn-reference#one-time
    private static InAppPurchaseEventType MapEventType(int notificationType) => notificationType switch
    {
        1 => InAppPurchaseEventType.Purchased,   // ONE_TIME_PRODUCT_PURCHASED
        2 => InAppPurchaseEventType.Cancelled,   // ONE_TIME_PRODUCT_CANCELED
        _ => InAppPurchaseEventType.Other
    };
}
