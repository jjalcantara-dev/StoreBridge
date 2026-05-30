namespace StoreBridge;

/// <summary>Normalized one-time purchase status across Apple and Google.</summary>
public enum PurchaseStatus
{
    /// <summary>The purchase was completed and is valid.</summary>
    Purchased,
    /// <summary>The purchase was cancelled before completion.</summary>
    Cancelled,
    /// <summary>The purchase is pending (e.g. awaiting parental approval or bank confirmation).</summary>
    Pending,
    /// <summary>The purchase was refunded or revoked by the store.</summary>
    Refunded,
    /// <summary>The purchase has been consumed (Google Play only).</summary>
    Consumed,
    /// <summary>The status could not be determined.</summary>
    Unknown
}
