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
    /// <summary>Subscription was cancelled.</summary>
    Cancelled,
    /// <summary>A new subscription was created.</summary>
    Created,
    /// <summary>Billing failed and the subscription entered account hold (beyond the grace period).</summary>
    InBillingRetry,
    /// <summary>A test notification triggered from App Store Connect or Play Console to verify the webhook wiring.</summary>
    Test,
    /// <summary>An event type not mapped by this library.</summary>
    Other
}
