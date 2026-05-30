using System.Text.Json.Serialization;

namespace StoreBridge.Apple.Internal;

internal sealed class AppStoreSubscriptionResponse
{
    [JsonPropertyName("data")]
    public List<SubscriptionGroupData>? Data { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }
}

internal sealed class SubscriptionGroupData
{
    [JsonPropertyName("subscriptionGroupIdentifier")]
    public string? SubscriptionGroupIdentifier { get; set; }

    [JsonPropertyName("lastTransactions")]
    public List<LastTransaction>? LastTransactions { get; set; }
}

internal sealed class LastTransaction
{
    [JsonPropertyName("originalTransactionId")]
    public string? OriginalTransactionId { get; set; }

    [JsonPropertyName("productId")]
    public string? ProductId { get; set; }

    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("signedTransactionInfo")]
    public string? SignedTransactionInfo { get; set; }

    [JsonPropertyName("signedRenewalInfo")]
    public string? SignedRenewalInfo { get; set; }
}

internal sealed class DecodedTransactionInfo
{
    [JsonPropertyName("originalTransactionId")]
    public string? OriginalTransactionId { get; set; }

    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; set; }

    [JsonPropertyName("productId")]
    public string? ProductId { get; set; }

    [JsonPropertyName("expiresDate")]
    public long? ExpiresDate { get; set; }

    [JsonPropertyName("purchaseDate")]
    public long? PurchaseDate { get; set; }

    [JsonPropertyName("price")]
    public long? Price { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("offerIdentifier")]
    public string? OfferIdentifier { get; set; }

    [JsonPropertyName("offerType")]
    public int? OfferType { get; set; }

    [JsonPropertyName("offerDiscountType")]
    public string? OfferDiscountType { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("quantity")]
    public int? Quantity { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("revocationDate")]
    public long? RevocationDate { get; set; }

    [JsonPropertyName("revocationReason")]
    public int? RevocationReason { get; set; }
}

internal sealed class DecodedRenewalInfo
{
    [JsonPropertyName("autoRenewStatus")]
    public int? AutoRenewStatus { get; set; }

    [JsonPropertyName("autoRenewProductId")]
    public string? AutoRenewProductId { get; set; }

    [JsonPropertyName("expirationReason")]
    public string? ExpirationReason { get; set; }

    [JsonPropertyName("isInBillingRetryPeriod")]
    public bool? IsInBillingRetryPeriod { get; set; }

    [JsonPropertyName("gracePeriodExpiresDate")]
    public long? GracePeriodExpiresDate { get; set; }
}

internal sealed class AppStoreTransactionResponse
{
    [JsonPropertyName("signedTransactionInfo")]
    public string? SignedTransactionInfo { get; set; }
}

// App Store Server Notifications v2 models
internal sealed class AppStoreNotificationPayload
{
    [JsonPropertyName("notificationType")]
    public string? NotificationType { get; set; }

    [JsonPropertyName("subtype")]
    public string? Subtype { get; set; }

    [JsonPropertyName("data")]
    public NotificationData? Data { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("signedDate")]
    public long? SignedDate { get; set; }

    [JsonPropertyName("notificationUUID")]
    public string? NotificationUUID { get; set; }
}

internal sealed class NotificationData
{
    [JsonPropertyName("bundleId")]
    public string? BundleId { get; set; }

    [JsonPropertyName("environment")]
    public string? Environment { get; set; }

    [JsonPropertyName("status")]
    public int? Status { get; set; }

    [JsonPropertyName("signedTransactionInfo")]
    public string? SignedTransactionInfo { get; set; }

    [JsonPropertyName("signedRenewalInfo")]
    public string? SignedRenewalInfo { get; set; }
}
