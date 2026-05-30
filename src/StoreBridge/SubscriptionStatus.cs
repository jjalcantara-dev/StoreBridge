namespace StoreBridge;

/// <summary>Normalized subscription lifecycle status across Apple and Google.</summary>
public enum SubscriptionStatus
{
    /// <summary>The subscription is active and in good standing.</summary>
    Active,
    /// <summary>The subscription has passed its expiry date.</summary>
    Expired,
    /// <summary>The user has cancelled; access may continue until the period ends.</summary>
    Cancelled,
    /// <summary>Billing failed but the subscription remains active in a grace period.</summary>
    InGracePeriod,
    /// <summary>Billing failed and the grace period has ended; access is suspended.</summary>
    InBillingRetry,
    /// <summary>The subscription was revoked by the store (e.g. refund or family-sharing removed).</summary>
    Revoked,
    /// <summary>The status could not be determined.</summary>
    Unknown
}
