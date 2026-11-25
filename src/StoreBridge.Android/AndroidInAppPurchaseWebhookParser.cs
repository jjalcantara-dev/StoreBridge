using StoreBridge.Android.Internal;

namespace StoreBridge.Android;

/// <summary>
/// Parses Google Play Real-time Developer Notifications for one-time in-app purchase events
/// delivered via Pub/Sub: <c>oneTimeProductNotification</c> (PURCHASED / CANCELED),
/// <c>voidedPurchaseNotification</c> (refunds / chargebacks), <c>pendingRefundReviewNotification</c>
/// (chargeback awaiting your review) and <c>testNotification</c>.
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
        cancellationToken.ThrowIfCancellationRequested();

        var (notification, messageId) = AndroidPubSubReader.Parse(rawBody);
        var eventAt = AndroidPubSubReader.FromUnixTimeMilliseconds(notification.EventTimeMillis);

        // testNotification is a wiring check sent from Play Console; it has no purchase payload
        if (notification.TestNotification != null && notification.OneTimeProductNotification == null
            && notification.VoidedPurchaseNotification == null && notification.PendingRefundReviewNotification == null)
        {
            return Task.FromResult(new InAppPurchaseNotification
            {
                Store = Store,
                EventType = InAppPurchaseEventType.Test,
                RawEventType = "TEST",
                NotificationId = messageId,
                EventAt = eventAt,
                IsSandbox = false
            });
        }

        // voidedPurchaseNotification covers refunds/chargebacks: subscriptions (productType == 1) belong to
        // AndroidWebhookParser; one-time purchases (productType == 2, or unspecified 0) are handled here.
        if (notification.VoidedPurchaseNotification is { } voided)
        {
            if (voided.ProductType == VoidedProductType.Subscription)
                throw new FormatException(
                    "Voided subscription notifications (productType = 1) are handled by AndroidWebhookParser.");

            return Task.FromResult(new InAppPurchaseNotification
            {
                Store = Store,
                EventType = InAppPurchaseEventType.Refunded,
                RawEventType = $"VOIDED:{voided.RefundType}:{voided.ProductType}",
                NotificationId = messageId,
                PurchaseId = voided.PurchaseToken ?? string.Empty,
                OrderId = voided.OrderId ?? string.Empty,
                EventAt = eventAt,
                IsSandbox = false
            });
        }

        // A chargeback Google wants you to review: respond via orders.reviewRefund within 24 hours
        if (notification.PendingRefundReviewNotification is { } review)
        {
            return Task.FromResult(new InAppPurchaseNotification
            {
                Store = Store,
                EventType = InAppPurchaseEventType.RefundReviewRequested,
                RawEventType = $"PENDING_REFUND_REVIEW:{review.RefundReason}",
                NotificationId = messageId,
                OrderId = review.OrderId ?? string.Empty,
                RefundReviewToken = review.PendingRefundToken ?? string.Empty,
                EventAt = eventAt,
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
            EventAt = eventAt,
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
