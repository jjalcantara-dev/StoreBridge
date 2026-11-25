namespace StoreBridge.Apple;

/// <summary>
/// Configuration for <see cref="AppleWebhookAuthenticator"/>. A valid Apple signature only proves that
/// <em>Apple</em> signed the notification — not that it is for <em>your</em> app — so the authenticator also
/// checks the app identity carried in the payload, as Apple's own App Store Server Libraries do.
/// </summary>
public sealed class AppleWebhookAuthenticatorOptions
{
    /// <summary>
    /// Required. Your app's bundle identifier (e.g. "com.example.app"). Notifications — and their nested
    /// signed transactions — for any other bundle are rejected.
    /// </summary>
    public string BundleId { get; set; } = string.Empty;

    /// <summary>
    /// Optional but recommended. Your app's Apple ID (App Store Connect → App Information → Apple ID).
    /// When set, production notifications must carry this <c>appAppleId</c>. Sandbox notifications never
    /// include it, so they are not checked against it.
    /// </summary>
    public long? AppAppleId { get; set; }

    /// <summary>
    /// Whether non-production notifications (<c>Sandbox</c>, <c>Xcode</c>, <c>LocalTesting</c>) are accepted.
    /// Default <see langword="true"/>: App Review and TestFlight purchases are sandbox transactions, and
    /// Apple sends them to your sandbox URL, which is often the same endpoint as production.
    /// </summary>
    public bool AllowSandbox { get; set; } = true;
}
