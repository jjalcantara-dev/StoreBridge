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
        if (string.IsNullOrWhiteSpace(rawBody))
            throw new ArgumentException("Webhook body cannot be empty.", nameof(rawBody));

        var outerPayload = AppleJwtHelper.ParseNotificationPayload(rawBody);

        DecodedTransactionInfo? transactionInfo = null;
        if (!string.IsNullOrEmpty(outerPayload.Data?.SignedTransactionInfo))
        {
            transactionInfo = AppleJwtHelper.DecodePayload<DecodedTransactionInfo>(
                outerPayload.Data.SignedTransactionInfo);
        }

        var notification = new SubscriptionNotification
        {
            Store = Store,
            EventType = MapEventType(outerPayload.NotificationType, outerPayload.Subtype),
            RawEventType = AppleJwtHelper.BuildRawEventType(outerPayload.NotificationType, outerPayload.Subtype),
            NotificationId = outerPayload.NotificationUUID ?? string.Empty,
            SubscriptionId = transactionInfo?.OriginalTransactionId ?? string.Empty,
            ProductId = transactionInfo?.ProductId ?? string.Empty,
            Status = AppleStatusMapper.Map(outerPayload.Data?.Status),
            ExpiresAt = transactionInfo?.ExpiresDate.HasValue == true
                ? DateTimeOffset.FromUnixTimeMilliseconds(transactionInfo.ExpiresDate!.Value)
                : null,
            EventAt = outerPayload.SignedDate.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(outerPayload.SignedDate.Value)
                : null,
            IsSandbox = string.Equals(outerPayload.Data?.Environment, "Sandbox", StringComparison.OrdinalIgnoreCase)
        };

        return Task.FromResult(notification);
    }

    private static NotificationEventType MapEventType(string? type, string? subtype) => type switch
    {
        "DID_RENEW" => NotificationEventType.Renewed,
        "SUBSCRIBED" => NotificationEventType.Created,
        "DID_CHANGE_RENEWAL_STATUS" when subtype == "AUTO_RENEW_DISABLED" => NotificationEventType.AutoRenewDisabled,
        "DID_CHANGE_RENEWAL_STATUS" => NotificationEventType.AutoRenewEnabled,
        "EXPIRED" => NotificationEventType.Expired,
        "DID_FAIL_TO_RENEW" when subtype == "GRACE_PERIOD" => NotificationEventType.GracePeriod,
        "DID_FAIL_TO_RENEW" => NotificationEventType.Expired,
        // Grace period ended — subscription entered account hold
        "GRACE_PERIOD_EXPIRED" => NotificationEventType.InBillingRetry,
        "REFUND" => NotificationEventType.Refunded,
        "REVOKE" => NotificationEventType.Refunded,
        "OFFER_REDEEMED" => NotificationEventType.Renewed,
        "CONSUMPTION_REQUEST" => NotificationEventType.Other,
        "TEST" => NotificationEventType.Test,
        _ => NotificationEventType.Other
    };
}
