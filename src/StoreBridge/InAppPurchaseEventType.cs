namespace StoreBridge;

/// <summary>Normalized event type for one-time in-app purchase notifications.</summary>
public enum InAppPurchaseEventType
{
    /// <summary>A new one-time purchase was completed.</summary>
    Purchased,
    /// <summary>A purchase was refunded or revoked by the store.</summary>
    Refunded,
    /// <summary>A purchase was cancelled before completion.</summary>
    Cancelled,
    /// <summary>Apple sent a consumption request for a consumable product.</summary>
    ConsumptionRequest,
    /// <summary>A test notification triggered from App Store Connect or Play Console to verify the webhook wiring.</summary>
    Test,
    /// <summary>An event type not mapped by this library.</summary>
    Other,
    /// <summary>
    /// The store reversed a previously granted refund (Apple <c>REFUND_REVERSED</c>).
    /// Reinstate the content you revoked because of the refund.
    /// </summary>
    /// <remarks>Appended after <see cref="Other"/> so existing numeric values stay stable.</remarks>
    RefundReversed,
    /// <summary>
    /// Google Play asks you to review a chargeback (<c>pendingRefundReviewNotification</c>).
    /// Respond within 24 hours by calling the Play Developer API <c>orders.reviewRefund</c> with
    /// <see cref="InAppPurchaseNotification.RefundReviewToken"/>.
    /// </summary>
    RefundReviewRequested
}
