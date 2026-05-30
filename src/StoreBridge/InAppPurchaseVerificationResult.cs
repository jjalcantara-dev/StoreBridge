namespace StoreBridge;

/// <summary>
/// Result of verifying a one-time in-app purchase against the store API.
/// </summary>
public sealed class InAppPurchaseVerificationResult
{
    /// <summary>Whether the verification call itself succeeded (not necessarily that the purchase is valid).</summary>
    public bool IsVerified { get; init; }

    /// <summary>The store that issued this result.</summary>
    public Store Store { get; init; }

    /// <summary>Normalized purchase status.</summary>
    public PurchaseStatus Status { get; init; }

    /// <summary>Platform-specific identifier: transactionId (Apple) or purchaseToken (Google).</summary>
    public string PurchaseId { get; init; } = string.Empty;

    /// <summary>The product identifier of the purchased item.</summary>
    public string ProductId { get; init; } = string.Empty;

    /// <summary>When the purchase was made.</summary>
    public DateTimeOffset? PurchasedAt { get; init; }

    /// <summary>Number of units purchased. Relevant for consumables; defaults to 1.</summary>
    public int Quantity { get; init; } = 1;

    /// <summary>Google only: whether the purchase has been acknowledged by the app.</summary>
    public bool IsAcknowledged { get; init; }

    /// <summary>Price in the smallest currency unit (Apple: thousandths, Google: micros). Use <see cref="PriceDecimal"/> for display.</summary>
    public long PriceAmount { get; init; }

    /// <summary>Price as a human-readable decimal.</summary>
    public decimal PriceDecimal { get; init; }

    /// <summary>ISO 4217 currency code (e.g. "USD", "EUR").</summary>
    public string CurrencyCode { get; init; } = string.Empty;

    /// <summary>Whether this transaction came from a sandbox/test environment.</summary>
    public bool IsSandbox { get; init; }

    /// <summary>Error description when <see cref="IsVerified"/> is <see langword="false"/>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Creates a failed result with no purchase data.</summary>
    public static InAppPurchaseVerificationResult Failure(Store store, string errorMessage) => new()
    {
        IsVerified = false,
        Store = store,
        Status = PurchaseStatus.Unknown,
        ErrorMessage = errorMessage
    };
}
