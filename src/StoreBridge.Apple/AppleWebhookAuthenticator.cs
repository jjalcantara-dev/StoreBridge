using StoreBridge.Apple.Internal;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace StoreBridge.Apple;

/// <summary>
/// Verifies the cryptographic authenticity of App Store Server Notifications v2.
/// Validates the X.509 certificate chain (x5c) against Apple Root CA - G3 and
/// verifies the ECDSA (ES256) signature of the signed payload JWT — including the
/// nested <c>signedTransactionInfo</c> and <c>signedRenewalInfo</c> JWTs.
/// </summary>
public sealed class AppleWebhookAuthenticator : IWebhookAuthenticator, IDisposable
{
    // Apple Root CA - G3 (EC P-384, valid 2014–2039)
    // Source:      https://www.apple.com/certificateauthority/AppleRootCA-G3.cer
    // SHA-256:     63343ABFB89A6A03EBB57E9B3F5FA7BE7C4F5C756F3017B3A8C488C3653E9179
    // Verify this value against Apple's Certificate Authority page before deploying to production.
    private static readonly byte[] AppleRootCaG3Der = Convert.FromBase64String(
        "MIICQzCCAcmgAwIBAgIILcX8iNLFS5UwCgYIKoZIzj0EAwMwZzEbMBkGA1UEAwwS" +
        "QXBwbGUgUm9vdCBDQSAtIEczMSYwJAYDVQQLDB1BcHBsZSBDZXJ0aWZpY2F0aW9u" +
        "IEF1dGhvcml0eTETMBEGA1UECgwKQXBwbGUgSW5jLjELMAkGA1UEBhMCVVMwHhcN" +
        "MTQwNDMwMTgxOTA2WhcNMzkwNDMwMTgxOTA2WjBnMRswGQYDVQQDDBJBcHBsZSBS" +
        "b290IENBIC0gRzMxJjAkBgNVBAsMHUFwcGxlIENlcnRpZmljYXRpb24gQXV0aG9y" +
        "aXR5MRMwEQYDVQQKDApBcHBsZSBJbmMuMQswCQYDVQQGEwJVUzB2MBAGByqGSM49" +
        "AgEGBSuBBAAiA2IABJjpLz1AcqTtkyJygRMc3RCV8cWjTnHcFBbZDuWmBSp3ZHtf" +
        "TjjTuxxEtX/1H7YyYl3J6YRbTzBPEVoA/VhYDKX1DyxNB0cTddqXl5dvMVztK517" +
        "IDvYuVTZXpmkOlEKMaNCMEAwHQYDVR0OBBYEFLuw3qFYM4iapIqZ3r6966/ayySr" +
        "MA8GA1UdEwEB/wQFMAMBAf8wDgYDVR0PAQH/BAQDAgEGMAoGCCqGSM49BAMDA2gA" +
        "MGUCMQCD6cHEFl4aXTQY2e3v9GwOAEZLuN+yRhHFD/3meoyhpmvOwgPUnPWTxnS4" +
        "at+qIxUCMG1mihDK1A3UT82NQz60imOlM27jbdoXt2QfyFMm+YhidDkLF1vLUagM" +
        "6BgD56KyKA=="
    );

    private readonly List<X509Certificate2> _trustedRoots;

    /// <inheritdoc />
    public Store Store => Store.Apple;

    /// <summary>
    /// Uses the built-in Apple Root CA - G3 as the trust anchor.
    /// </summary>
    public AppleWebhookAuthenticator()
        : this([AppleRootCaG3Der]) { }

    /// <summary>
    /// Uses a custom DER-encoded root CA certificate as the trust anchor.
    /// Useful for testing or when Apple updates their root CA.
    /// </summary>
    /// <param name="trustedRootDer">
    /// DER-encoded root CA certificate bytes.
    /// Download the official cert from https://www.apple.com/certificateauthority/
    /// </param>
    public AppleWebhookAuthenticator(byte[] trustedRootDer)
        : this([trustedRootDer ?? throw new ArgumentNullException(nameof(trustedRootDer))]) { }

    /// <summary>
    /// Uses one or more DER-encoded root CA certificates as trust anchors. A notification is
    /// accepted if its certificate chain anchors to <em>any</em> of the supplied roots — useful
    /// during an Apple root CA rotation when both the old and new roots must be trusted.
    /// </summary>
    /// <param name="trustedRootsDer">DER-encoded root CA certificate bytes.</param>
    public AppleWebhookAuthenticator(IEnumerable<byte[]> trustedRootsDer)
    {
        ArgumentNullException.ThrowIfNull(trustedRootsDer);

        _trustedRoots = trustedRootsDer
            .Select(der => der ?? throw new ArgumentException("Trusted root entry cannot be null.", nameof(trustedRootsDer)))
            .Select(LoadCertificate)
            .ToList();

        if (_trustedRoots.Count == 0)
            throw new ArgumentException("At least one trusted root certificate must be supplied.", nameof(trustedRootsDer));
    }

    private static X509Certificate2 LoadCertificate(byte[] der) =>
#if NET9_0_OR_GREATER
        X509CertificateLoader.LoadCertificate(der);
#else
        new(der);
#endif

    /// <summary>
    /// Validates the App Store Server Notification by verifying the X.509 certificate
    /// chain (x5c) and the ECDSA signature of the signed payload JWT, then verifying the
    /// nested <c>signedTransactionInfo</c> and <c>signedRenewalInfo</c> JWTs the same way.
    /// The <paramref name="bearerToken"/> parameter is unused for Apple — the signature
    /// is self-contained inside the JWT.
    /// </summary>
    /// <param name="rawBody">The raw webhook request body.</param>
    /// <param name="bearerToken">Unused for Apple.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="WebhookAuthenticationException">
    /// Thrown when a certificate chain is invalid, does not chain to a trusted Apple root,
    /// or when a JWT signature does not match.
    /// </exception>
    public Task ValidateAsync(string rawBody, string? bearerToken = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            throw new ArgumentException("Webhook body cannot be empty.", nameof(rawBody));

        var signedPayload = ExtractSignedPayload(rawBody);
        AppleCertificateChainValidator.ValidateSignature(signedPayload, _trustedRoots);

        foreach (var nested in ExtractNestedSignedJwts(signedPayload))
            AppleCertificateChainValidator.ValidateSignature(nested, _trustedRoots);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var root in _trustedRoots)
            root.Dispose();
    }

    private static string ExtractSignedPayload(string rawBody)
    {
        if (!rawBody.TrimStart().StartsWith('{'))
            return rawBody.Trim();

        using var doc = JsonDocument.Parse(rawBody);
        if (!doc.RootElement.TryGetProperty("signedPayload", out var sp))
            throw new FormatException("Missing 'signedPayload' field in webhook body.");

        return sp.GetString() ?? throw new FormatException("'signedPayload' is null.");
    }

    /// <summary>
    /// Decodes the (already signature-checked) outer payload and returns the nested
    /// <c>signedTransactionInfo</c> / <c>signedRenewalInfo</c> JWTs that are present.
    /// </summary>
    private static IEnumerable<string> ExtractNestedSignedJwts(string outerJwt)
    {
        var parts = outerJwt.Split('.');
        if (parts.Length != 3)
            throw new WebhookAuthenticationException("Invalid JWT format: expected 3 parts.");

        var payloadBytes = Base64UrlDecode(parts[1]);
        using var doc = JsonDocument.Parse(payloadBytes);

        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            yield break;

        if (data.TryGetProperty("signedTransactionInfo", out var tx)
            && tx.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(tx.GetString()))
        {
            yield return tx.GetString()!;
        }

        if (data.TryGetProperty("signedRenewalInfo", out var renewal)
            && renewal.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(renewal.GetString()))
        {
            yield return renewal.GetString()!;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch
        {
            2 => s + "==",
            3 => s + "=",
            _ => s
        };
        return Convert.FromBase64String(s);
    }
}
