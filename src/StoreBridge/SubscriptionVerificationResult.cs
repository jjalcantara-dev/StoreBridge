namespace StoreBridge;

/// <summary>
/// Result of verifying a subscription receipt or purchase token against the store API.
/// </summary>
public sealed class SubscriptionVerificationResult
{
    /// <summary>Whether the verification call itself succeeded (not necessarily that the subscription is active).</summary>
    public bool IsVerified { get; init; }

    /// <summary>The store that issued this result.</summary>
    public Store Store { get; init; }

    /// <summary>Normalized subscription lifecycle status.</summary>
    public SubscriptionStatus Status { get; init; }

    /// <summary>Platform-specific unique identifier (originalTransactionId for Apple, purchaseToken for Google).</summary>
    public string SubscriptionId { get; init; } = string.Empty;

    /// <summary>The subscription product identifier.</summary>
    public string ProductId { get; init; } = string.Empty;

    /// <summary>When the current subscription period expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When the subscription was originally purchased.</summary>
    public DateTimeOffset? PurchasedAt { get; init; }

    /// <summary>Whether this is a trial, promotional, or introductory-price period.</summary>
    public bool IsPromotional { get; init; }

    /// <summary>Price in the smallest currency unit (Apple: thousandths, Google: micros). Use <see cref="PriceDecimal"/> for display.</summary>
    public long PriceAmount { get; init; }

    /// <summary>ISO 4217 currency code (e.g. "USD", "EUR").</summary>
    public string CurrencyCode { get; init; } = string.Empty;

    /// <summary>Price as a human-readable decimal.</summary>
    public decimal PriceDecimal { get; init; }

    /// <summary>Whether the subscription was cancelled by the user (auto-renew disabled).</summary>
    public bool CancelledByUser { get; init; }

    /// <summary>
    /// The product the subscription will renew into at the next billing cycle.
    /// Differs from <see cref="ProductId"/> when the user scheduled an upgrade or downgrade.
    /// Apple only; empty for Google.
    /// </summary>
    public string AutoRenewProductId { get; init; } = string.Empty;

    /// <summary>
    /// When the current grace period ends, if the subscription is in one (billing failed, access retained).
    /// Apple only; <see langword="null"/> for Google.
    /// </summary>
    public DateTimeOffset? GracePeriodExpiresAt { get; init; }

    /// <summary>Whether this transaction came from a sandbox/test environment.</summary>
    public bool IsSandbox { get; init; }

    /// <summary>Error description when <see cref="IsVerified"/> is <see langword="false"/>.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Creates a failed result with no subscription data.</summary>
    public static SubscriptionVerificationResult Failure(Store store, string errorMessage) => new()
    {
        IsVerified = false,
        Store = store,
        Status = SubscriptionStatus.Unknown,
        ErrorMessage = errorMessage
    };
}
