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

    /// <summary>
    /// When <see langword="true"/>, performs online OCSP/CRL revocation checks on Apple's signing certificate
    /// chain after verifying marker OIDs and custom-root anchoring. Default: <see langword="false"/>
    /// (avoids outbound network latency on every webhook).
    /// </summary>
    public bool EnableOnlineRevocationCheck { get; set; }

    /// <summary>
    /// When <see langword="true"/>, evaluates each JWS certificate chain's validity window at its own
    /// <c>signedDate</c> (when present) instead of the current UTC time — matching Apple's official App Store
    /// Server Libraries so retried deliveries or older nested transactions signed before a leaf rotation
    /// remain verifiable. Default: <see langword="false"/>.
    /// </summary>
    public bool ValidateCertificateAtSignedDate { get; set; }
}
