using StoreBridge.Apple.Internal;

namespace StoreBridge.Apple;

/// <summary>
/// Parses App Store Server Notifications v2 for one-time in-app purchase events
/// (ONE_TIME_CHARGE, REFUND, REFUND_REVERSED, REVOKE, CONSUMPTION_REQUEST).
/// </summary>
public sealed class AppleInAppPurchaseWebhookParser : IInAppPurchaseWebhookParser
{
    /// <inheritdoc />
    public Store Store => Store.Apple;

    /// <summary>
    /// Parses the signed payload from an App Store Server Notification.
    /// </summary>
    /// <param name="rawBody">
    /// The raw JSON body: <c>{ "signedPayload": "eyJ..." }</c>
    /// or the signed JWT string directly.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<InAppPurchaseNotification> ParseAsync(string rawBody, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            throw new ArgumentException("Webhook body cannot be empty.", nameof(rawBody));

        var outerPayload = AppleJwtHelper.ParseNotificationPayload(rawBody);

        DecodedTransactionInfo? transactionInfo = null;
        if (!string.IsNullOrEmpty(outerPayload.Data?.SignedTransactionInfo))
        {
            transactionInfo = AppleJwtHelper.DecodePayload<DecodedTransactionInfo>(
                outerPayload.Data.SignedTransactionInfo);
        }

        var notification = new InAppPurchaseNotification
        {
            Store = Store,
            EventType = MapEventType(outerPayload.NotificationType),
            RawEventType = AppleJwtHelper.BuildRawEventType(outerPayload.NotificationType, outerPayload.Subtype),
            NotificationId = outerPayload.NotificationUUID ?? string.Empty,
            PurchaseId = transactionInfo?.TransactionId ?? string.Empty,
            ProductId = transactionInfo?.ProductId ?? string.Empty,
            EventAt = outerPayload.SignedDate.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(outerPayload.SignedDate.Value)
                : null,
            IsSandbox = AppleJwtHelper.IsNonProduction(outerPayload.Data?.Environment)
        };

        return Task.FromResult(notification);
    }

    // https://developer.apple.com/documentation/appstoreservernotifications/notificationtype
    private static InAppPurchaseEventType MapEventType(string? type) => type switch
    {
        // Consumable, non-consumable or non-renewing subscription purchased (also Family Sharing grants)
        "ONE_TIME_CHARGE" => InAppPurchaseEventType.Purchased,
        "REFUND" => InAppPurchaseEventType.Refunded,
        // A refund was reversed after a dispute — reinstate the content
        "REFUND_REVERSED" => InAppPurchaseEventType.RefundReversed,
        "REVOKE" => InAppPurchaseEventType.Refunded,
        "CONSUMPTION_REQUEST" => InAppPurchaseEventType.ConsumptionRequest,
        "TEST" => InAppPurchaseEventType.Test,
        _ => InAppPurchaseEventType.Other
    };
}
