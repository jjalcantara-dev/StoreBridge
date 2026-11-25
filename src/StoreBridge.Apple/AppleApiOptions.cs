namespace StoreBridge.Apple;

/// <summary>
/// Common authentication options for all Apple App Store Server API interactions.
/// All values are available in App Store Connect → Users and Access → Keys.
/// </summary>
public abstract class AppleApiOptions
{
    /// <summary>The Key ID from App Store Connect (e.g. "ABC1234567").</summary>
    public string KeyId { get; set; } = string.Empty;

    /// <summary>The Issuer ID (your team's UUID) from App Store Connect.</summary>
    public string IssuerId { get; set; } = string.Empty;

    /// <summary>Your app's bundle identifier (e.g. "com.example.app").</summary>
    public string BundleId { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded PKCS#8 private key (.p8 file) downloaded from App Store Connect.
    /// Strip the PEM header/footer lines and base64-encode the raw bytes.
    /// </summary>
    public string PrivateKeyBase64 { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of attempts (including the initial call) when the Apple API returns a transient error
    /// (5xx, 429, network failure, timeout). Default: 3.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Per-attempt HTTP request timeout for calls to the App Store Server API. Default: 30 seconds.
    /// Applied to the internally created <see cref="HttpClient"/> and to the named client registered via DI.
    /// </summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When <see langword="true"/> (default) and the primary endpoint answers <c>404 Not Found</c>, the lookup
    /// is retried once against the sandbox endpoint. This is what Apple recommends: App Review and TestFlight
    /// purchases are sandbox transactions even when your server talks to production.
    /// Has no effect when the primary endpoint already is the sandbox endpoint.
    /// </summary>
    /// <remarks>
    /// Because sandbox transactions verify with <c>IsVerified = true</c> and <c>IsSandbox = true</c>,
    /// production code should check <c>!result.IsSandbox</c> before granting paid entitlements unless
    /// sandbox/TestFlight access is intentionally allowed.
    /// </remarks>
    public bool SandboxFallback { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, cryptographically verifies the <c>x5c</c> certificate chain and ES256
    /// signature of the JWS payloads (<c>signedTransactionInfo</c> / <c>signedRenewalInfo</c>) returned by
    /// the App Store Server API against Apple Root CA - G3, in addition to TLS transport security.
    /// Default: <see langword="false"/>.
    /// </summary>
    public bool VerifyResponseSignatures { get; set; }
}
