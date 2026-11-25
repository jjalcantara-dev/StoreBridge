using Microsoft.Extensions.Options;
using StoreBridge.Apple.Internal;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace StoreBridge.Apple;

/// <summary>
/// Verifies the cryptographic authenticity of App Store Server Notifications v2.
/// Validates the X.509 certificate chain (x5c) against Apple Root CA - G3 — including Apple's marker
/// extensions on the leaf and intermediate certificates — verifies the ECDSA (ES256) signature of the
/// signed payload JWT and of the nested <c>signedTransactionInfo</c> / <c>signedRenewalInfo</c> JWTs, and
/// checks that the notification belongs to the configured app (bundle ID, environment, app Apple ID).
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

    private readonly AppleWebhookAuthenticatorOptions _options;
    private readonly List<X509Certificate2> _trustedRoots;

    /// <inheritdoc />
    public Store Store => Store.Apple;

    /// <summary>
    /// Uses the built-in Apple Root CA - G3 as the trust anchor.
    /// </summary>
    /// <param name="options">App identity the notifications must belong to. <see cref="AppleWebhookAuthenticatorOptions.BundleId"/> is required.</param>
    public AppleWebhookAuthenticator(AppleWebhookAuthenticatorOptions options)
        : this(options, [AppleRootCaG3Der]) { }

    /// <summary>
    /// Uses one or more DER-encoded root CA certificates as trust anchors instead of the built-in one.
    /// A notification is accepted if its certificate chain anchors to <em>any</em> of the supplied roots —
    /// useful during an Apple root CA rotation when both the old and new roots must be trusted, or in tests.
    /// </summary>
    /// <param name="options">App identity the notifications must belong to. <see cref="AppleWebhookAuthenticatorOptions.BundleId"/> is required.</param>
    /// <param name="trustedRootsDer">
    /// DER-encoded root CA certificate bytes. Download the official certs from https://www.apple.com/certificateauthority/
    /// </param>
    public AppleWebhookAuthenticator(AppleWebhookAuthenticatorOptions options, IEnumerable<byte[]> trustedRootsDer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(trustedRootsDer);

        if (string.IsNullOrWhiteSpace(options.BundleId))
            throw new ArgumentException(
                $"{nameof(AppleWebhookAuthenticatorOptions.BundleId)} must be set.", nameof(options));

        _options = options;
        _trustedRoots = trustedRootsDer
            .Select(der => der ?? throw new ArgumentException("Trusted root entry cannot be null.", nameof(trustedRootsDer)))
            .Select(X509CertificateLoader.LoadCertificate)
            .ToList();

        if (_trustedRoots.Count == 0)
            throw new ArgumentException("At least one trusted root certificate must be supplied.", nameof(trustedRootsDer));
    }

    /// <summary>DI-friendly constructor that resolves options from <see cref="IOptions{TOptions}"/>.</summary>
    internal AppleWebhookAuthenticator(IOptions<AppleWebhookAuthenticatorOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options))) { }

    /// <summary>
    /// Validates the App Store Server Notification: certificate chain and ECDSA signature of the signed
    /// payload JWT and of the nested <c>signedTransactionInfo</c> / <c>signedRenewalInfo</c> JWTs, then the
    /// app identity (bundle ID, environment, app Apple ID). The <paramref name="bearerToken"/> parameter is
    /// unused for Apple — the signature is self-contained inside the JWT.
    /// </summary>
    /// <param name="rawBody">The raw webhook request body.</param>
    /// <param name="bearerToken">Unused for Apple.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="WebhookAuthenticationException">
    /// Thrown when a certificate chain is invalid or does not chain to a trusted Apple root, when a JWT
    /// signature does not match, or when the notification is for another app or a disallowed environment.
    /// </exception>
    public Task ValidateAsync(string rawBody, string? bearerToken = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawBody))
            throw new ArgumentException("Webhook body cannot be empty.", nameof(rawBody));

        cancellationToken.ThrowIfCancellationRequested();

        var signedPayload = AppleJwtHelper.ExtractSignedPayload(rawBody);
        AppleCertificateChainValidator.ValidateSignature(signedPayload, _trustedRoots);

        AppStoreNotificationPayload payload;
        try
        {
            payload = AppleJwtHelper.DecodePayload<AppStoreNotificationPayload>(signedPayload)
                ?? throw new WebhookAuthenticationException("Apple notification payload is empty.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new WebhookAuthenticationException("Apple notification payload could not be decoded.", ex);
        }

        var environment = ValidateAppIdentity(payload);

        if (!string.IsNullOrEmpty(payload.Data?.SignedTransactionInfo))
        {
            AppleCertificateChainValidator.ValidateSignature(payload.Data.SignedTransactionInfo, _trustedRoots);
            ValidateNestedTransaction(payload.Data.SignedTransactionInfo, environment);
        }

        if (!string.IsNullOrEmpty(payload.Data?.SignedRenewalInfo))
            AppleCertificateChainValidator.ValidateSignature(payload.Data.SignedRenewalInfo, _trustedRoots);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var root in _trustedRoots)
            root.Dispose();
    }

    /// <summary>Checks bundle ID, environment and app Apple ID of the outer payload. Returns the environment.</summary>
    private string? ValidateAppIdentity(AppStoreNotificationPayload payload)
    {
        string? bundleId;
        string? environment;
        long? appAppleId;

        if (payload.Data is { } data)
        {
            (bundleId, environment, appAppleId) = (data.BundleId, data.Environment, data.AppAppleId);
        }
        else if (payload.Summary is { } summary)
        {
            (bundleId, environment, appAppleId) = (summary.BundleId, summary.Environment, summary.AppAppleId);
        }
        else if (payload.ExternalPurchaseToken is { } token)
        {
            // External purchase tokens carry no environment field; sandbox IDs are prefixed with "SANDBOX".
            var sandbox = token.ExternalPurchaseId?.StartsWith("SANDBOX", StringComparison.Ordinal) == true;
            (bundleId, environment, appAppleId) = (token.BundleId, sandbox ? "Sandbox" : "Production", token.AppAppleId);
        }
        else
        {
            throw new WebhookAuthenticationException(
                "Apple notification carries no app identity (no 'data', 'summary' or 'externalPurchaseToken').");
        }

        if (!string.Equals(bundleId, _options.BundleId, StringComparison.Ordinal))
            throw new WebhookAuthenticationException(
                $"Apple notification is for bundle '{bundleId ?? "(none)"}', expected '{_options.BundleId}'.");

        var nonProduction = AppleJwtHelper.IsNonProduction(environment);
        if (nonProduction && !_options.AllowSandbox)
            throw new WebhookAuthenticationException(
                $"Apple notification is from the '{environment}' environment and sandbox notifications are not allowed.");

        if (!nonProduction && _options.AppAppleId is { } expectedAppId && appAppleId != expectedAppId)
            throw new WebhookAuthenticationException(
                $"Apple notification is for app Apple ID '{appAppleId?.ToString() ?? "(none)"}', expected '{expectedAppId}'.");

        return environment;
    }

    private void ValidateNestedTransaction(string signedTransactionInfo, string? outerEnvironment)
    {
        DecodedTransactionInfo? tx;
        try
        {
            tx = AppleJwtHelper.DecodePayload<DecodedTransactionInfo>(signedTransactionInfo);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw new WebhookAuthenticationException("Apple signedTransactionInfo could not be decoded.", ex);
        }

        if (tx?.BundleId is { } txBundle && !string.Equals(txBundle, _options.BundleId, StringComparison.Ordinal))
            throw new WebhookAuthenticationException(
                $"Apple signedTransactionInfo is for bundle '{txBundle}', expected '{_options.BundleId}'.");

        if (tx?.Environment is { } txEnv && outerEnvironment is not null &&
            !string.Equals(txEnv, outerEnvironment, StringComparison.OrdinalIgnoreCase))
            throw new WebhookAuthenticationException(
                $"Apple signedTransactionInfo environment '{txEnv}' does not match the notification environment '{outerEnvironment}'.");
    }
}
