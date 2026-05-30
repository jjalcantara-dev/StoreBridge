namespace StoreBridge;

/// <summary>
/// Verifies a one-time in-app purchase token against the store server API.
/// </summary>
public interface IInAppPurchaseVerifier
{
    /// <summary>The store this verifier targets.</summary>
    Store Store { get; }

    /// <summary>
    /// Verifies a one-time in-app purchase and returns its current status.
    /// </summary>
    /// <param name="purchaseToken">
    /// For Apple: the transaction ID.
    /// For Google: the purchase token.
    /// </param>
    /// <param name="productId">
    /// For Google: required. The product SKU (e.g. "coins_100").
    /// For Apple: optional. Used to confirm the expected product.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<InAppPurchaseVerificationResult> VerifyPurchaseAsync(
        string purchaseToken,
        string? productId = null,
        CancellationToken cancellationToken = default);
}
