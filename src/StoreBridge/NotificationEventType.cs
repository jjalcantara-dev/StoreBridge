namespace StoreBridge;

/// <summary>Normalized subscription event type across Apple and Google server-to-server notifications.</summary>
public enum NotificationEventType
{
    /// <summary>Subscription renewed or became active.</summary>
    Renewed,
    /// <summary>User disabled auto-renew (subscription will expire at end of period).</summary>
    AutoRenewDisabled,
    /// <summary>User re-enabled auto-renew.</summary>
    AutoRenewEnabled,
    /// <summary>Subscription expired.</summary>
    Expired,
    /// <summary>Subscription is in a grace period (billing failed, still active).</summary>
    GracePeriod,
    /// <summary>Subscription was refunded or revoked by the store.</summary>
    Refunded,
    /// <summary>Subscription was cancelled (auto-renew turned off by the store, the user, or the developer).</summary>
    Cancelled,
    /// <summary>A new subscription was created.</summary>
    Created,
    /// <summary>
    /// Billing failed and the subscription entered the billing-retry period without (or after) a grace period
    /// — Apple <c>DID_FAIL_TO_RENEW</c> without subtype / <c>GRACE_PERIOD_EXPIRED</c>, Google <c>SUBSCRIPTION_ON_HOLD</c>.
    /// </summary>
    InBillingRetry,
    /// <summary>A test notification triggered from App Store Connect or Play Console to verify the webhook wiring.</summary>
    Test,
    /// <summary>An event type not mapped by this library.</summary>
    Other,
    /// <summary>
    /// The store reversed a previously granted refund (Apple <c>REFUND_REVERSED</c>).
    /// Reinstate any access you revoked because of the refund.
    /// </summary>
    /// <remarks>Appended after <see cref="Other"/> so existing numeric values stay stable.</remarks>
    RefundReversed
}
