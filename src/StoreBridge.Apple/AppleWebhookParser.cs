using StoreBridge.Apple.Internal;

namespace StoreBridge.Apple;

/// <summary>
/// Parses App Store Server Notifications v2 (signed JWT payload).
/// </summary>
public sealed class AppleWebhookParser : IWebhookParser
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
    public Task<SubscriptionNotification> ParseAsync(string rawBody, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(rawBody))
            throw new ArgumentException("Webhook body cannot be empty.", nameof(rawBody));

        var outerPayload = AppleJwtHelper.ParseNotificationPayload(rawBody);

        DecodedTransactionInfo? transactionInfo = null;
        if (!string.IsNullOrEmpty(outerPayload.Data?.SignedTransactionInfo))
        {
            transactionInfo = AppleJwtHelper.DecodePayload<DecodedTransactionInfo>(
                outerPayload.Data.SignedTransactionInfo);
        }

        DecodedRenewalInfo? renewalInfo = null;
        if (!string.IsNullOrEmpty(outerPayload.Data?.SignedRenewalInfo))
        {
            renewalInfo = AppleJwtHelper.DecodePayload<DecodedRenewalInfo>(
                outerPayload.Data.SignedRenewalInfo);
        }

        var environment = outerPayload.Data?.Environment ?? outerPayload.Summary?.Environment;

        var notification = new SubscriptionNotification
        {
            Store = Store,
            EventType = MapEventType(outerPayload.NotificationType, outerPayload.Subtype),
            RawEventType = AppleJwtHelper.BuildRawEventType(outerPayload.NotificationType, outerPayload.Subtype),
            NotificationId = outerPayload.NotificationUUID ?? string.Empty,
            SubscriptionId = transactionInfo?.OriginalTransactionId ?? string.Empty,
            ProductId = transactionInfo?.ProductId ?? string.Empty,
            Status = AppleStatusMapper.Map(outerPayload.Data?.Status, autoRenewOff: renewalInfo?.AutoRenewStatus == 0),
            ExpiresAt = AppleJwtHelper.FromUnixTimeMilliseconds(transactionInfo?.ExpiresDate),
            EventAt = AppleJwtHelper.FromUnixTimeMilliseconds(outerPayload.SignedDate),
            IsSandbox = AppleJwtHelper.IsNonProduction(environment)
        };

        return Task.FromResult(notification);
    }

    // https://developer.apple.com/documentation/appstoreservernotifications/notificationtype
    private static NotificationEventType MapEventType(string? type, string? subtype) => type switch
    {
        "DID_RENEW" => NotificationEventType.Renewed,
        "SUBSCRIBED" => NotificationEventType.Created,
        "DID_CHANGE_RENEWAL_STATUS" when subtype == "AUTO_RENEW_DISABLED" => NotificationEventType.AutoRenewDisabled,
        "DID_CHANGE_RENEWAL_STATUS" when subtype == "AUTO_RENEW_ENABLED" => NotificationEventType.AutoRenewEnabled,
        "DID_CHANGE_RENEWAL_STATUS" => NotificationEventType.Other,
        "EXPIRED" => NotificationEventType.Expired,
        "DID_FAIL_TO_RENEW" when subtype == "GRACE_PERIOD" => NotificationEventType.GracePeriod,
        // No subtype: billing failed and the subscription entered billing retry without a grace period
        "DID_FAIL_TO_RENEW" => NotificationEventType.InBillingRetry,
        // Grace period ended — Apple keeps retrying billing (up to 60 days)
        "GRACE_PERIOD_EXPIRED" => NotificationEventType.InBillingRetry,
        "REFUND" => NotificationEventType.Refunded,
        "REVOKE" => NotificationEventType.Refunded,
        // A refund was reversed after a dispute — reinstate access
        "REFUND_REVERSED" => NotificationEventType.RefundReversed,
        "OFFER_REDEEMED" => NotificationEventType.Renewed,
        "CONSUMPTION_REQUEST" => NotificationEventType.Other,
        "TEST" => NotificationEventType.Test,
        _ => NotificationEventType.Other
    };
}
