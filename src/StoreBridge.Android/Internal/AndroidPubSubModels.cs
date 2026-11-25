using System.Text.Json.Serialization;

namespace StoreBridge.Android.Internal;

internal sealed class PubSubPushMessage
{
    [JsonPropertyName("message")]
    public PubSubMessage? Message { get; set; }

    [JsonPropertyName("subscription")]
    public string? Subscription { get; set; }
}

internal sealed class PubSubMessage
{
    [JsonPropertyName("data")]
    public string? Data { get; set; }

    [JsonPropertyName("messageId")]
    public string? MessageId { get; set; }

    [JsonPropertyName("publishTime")]
    public string? PublishTime { get; set; }
}

internal sealed class DeveloperNotification
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("packageName")]
    public string? PackageName { get; set; }

    [JsonPropertyName("eventTimeMillis")]
    public long? EventTimeMillis { get; set; }

    [JsonPropertyName("subscriptionNotification")]
    public SubscriptionNotificationPayload? SubscriptionNotification { get; set; }

    [JsonPropertyName("oneTimeProductNotification")]
    public OneTimeProductNotificationPayload? OneTimeProductNotification { get; set; }

    [JsonPropertyName("voidedPurchaseNotification")]
    public VoidedPurchaseNotificationPayload? VoidedPurchaseNotification { get; set; }

    [JsonPropertyName("pendingRefundReviewNotification")]
    public PendingRefundReviewNotificationPayload? PendingRefundReviewNotification { get; set; }

    [JsonPropertyName("testNotification")]
    public TestNotificationPayload? TestNotification { get; set; }
}

internal sealed class SubscriptionNotificationPayload
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("notificationType")]
    public int NotificationType { get; set; }

    [JsonPropertyName("purchaseToken")]
    public string? PurchaseToken { get; set; }

    /// <summary>
    /// Legacy field: no longer listed in Google's RTDN reference and may be absent. Query
    /// <c>purchases.subscriptionsv2.get</c> for the authoritative product ID.
    /// </summary>
    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }
}

internal sealed class OneTimeProductNotificationPayload
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("notificationType")]
    public int NotificationType { get; set; }

    [JsonPropertyName("purchaseToken")]
    public string? PurchaseToken { get; set; }

    [JsonPropertyName("sku")]
    public string? Sku { get; set; }
}

internal sealed class VoidedPurchaseNotificationPayload
{
    [JsonPropertyName("purchaseToken")]
    public string? PurchaseToken { get; set; }

    [JsonPropertyName("orderId")]
    public string? OrderId { get; set; }

    /// <summary>1 = full refund, 2 = partial (quantity-based) refund.</summary>
    [JsonPropertyName("refundType")]
    public int RefundType { get; set; }

    /// <summary>1 = PRODUCT_TYPE_SUBSCRIPTION, 2 = PRODUCT_TYPE_ONE_TIME.</summary>
    [JsonPropertyName("productType")]
    public int ProductType { get; set; }
}

internal static class VoidedProductType
{
    internal const int Subscription = 1;
    internal const int OneTime = 2;
}

/// <summary>A chargeback that needs developer review via <c>orders.reviewRefund</c> within 24 hours.</summary>
internal sealed class PendingRefundReviewNotificationPayload
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("pendingRefundToken")]
    public string? PendingRefundToken { get; set; }

    [JsonPropertyName("orderId")]
    public string? OrderId { get; set; }

    /// <summary>7 = CHARGEBACK (the only value supported today).</summary>
    [JsonPropertyName("refundReason")]
    public int RefundReason { get; set; }
}

internal sealed class TestNotificationPayload
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}