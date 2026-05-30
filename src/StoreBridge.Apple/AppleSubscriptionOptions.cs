namespace StoreBridge.Apple;

/// <summary>
/// Configuration for the App Store Server API subscriptions endpoint.
/// </summary>
public sealed class AppleSubscriptionOptions : AppleApiOptions
{
    /// <summary>
    /// Base URL for the App Store Server API subscriptions endpoint.
    /// Production: https://api.storekit.itunes.apple.com/inApps/v1/subscriptions/
    /// Sandbox:    https://api.storekit-sandbox.itunes.apple.com/inApps/v1/subscriptions/
    /// </summary>
    public string SubscriptionsBaseUrl { get; set; } = "https://api.storekit.itunes.apple.com/inApps/v1/subscriptions/";
}
