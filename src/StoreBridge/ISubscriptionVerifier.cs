namespace StoreBridge;

/// <summary>
/// Verifies a subscription receipt or purchase token against the store server API.
/// </summary>
public interface ISubscriptionVerifier
{
    /// <summary>The store this verifier targets.</summary>
    Store Store { get; }

    /// <summary>
    /// Verifies a subscription and returns its current status.
    /// </summary>
    /// <param name="receiptOrToken">
    /// For Apple: the original transaction ID.
    /// For Google: the purchase token.
    /// </param>
    /// <param name="productId">
    /// For Google: required. The expected subscription product ID, used to select and validate the matching line item.
    /// For Apple: optional, used to filter and validate when multiple products exist.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SubscriptionVerificationResult> VerifySubscriptionAsync(
        string receiptOrToken,
        string? productId = null,
        CancellationToken cancellationToken = default);
}
