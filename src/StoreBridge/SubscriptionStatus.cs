namespace StoreBridge;

/// <summary>Normalized subscription lifecycle status across Apple and Google.</summary>
public enum SubscriptionStatus
{
    /// <summary>The subscription is active and in good standing.</summary>
    Active,
    /// <summary>The subscription has passed its expiry date.</summary>
    Expired,
    /// <summary>
    /// Auto-renew is off (cancelled by the user, the store or the developer) but the paid period has not
    /// ended yet — the user keeps access until <c>ExpiresAt</c>.
    /// </summary>
    Cancelled,
    /// <summary>Billing failed but the subscription remains active in a grace period.</summary>
    InGracePeriod,
    /// <summary>Billing failed and the store is retrying without a grace period (or after it ended); access is suspended.</summary>
    InBillingRetry,
    /// <summary>The subscription was revoked by the store (e.g. refund or family-sharing removed).</summary>
    Revoked,
    /// <summary>The status could not be determined.</summary>
    Unknown
}
