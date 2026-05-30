namespace StoreBridge.Apple;

/// <summary>
/// Configuration for the App Store Server API transactions endpoint (one-time in-app purchases).
/// </summary>
public sealed class AppleInAppPurchaseOptions : AppleApiOptions
{
    /// <summary>
    /// Base URL for the App Store Server API transactions endpoint.
    /// Production: https://api.storekit.itunes.apple.com/inApps/v1/transactions/
    /// Sandbox:    https://api.storekit-sandbox.itunes.apple.com/inApps/v1/transactions/
    /// </summary>
    public string TransactionsBaseUrl { get; set; } = "https://api.storekit.itunes.apple.com/inApps/v1/transactions/";
}
