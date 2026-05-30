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
    Other
}
