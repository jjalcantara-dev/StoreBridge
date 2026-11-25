using Microsoft.Extensions.Options;
using System.Security.Cryptography;

namespace StoreBridge.Apple.Internal;

/// <summary>
/// Runs at the first <see cref="IOptions{TOptions}.Value"/> resolution so misconfigured Apple
/// credentials (missing fields, garbled base64, non-PKCS#8 key) fail fast instead of producing
/// opaque <see cref="CryptographicException"/> traces on the first verifier call.
/// </summary>
internal static class AppleOptionsValidator
{
    internal static ValidateOptionsResult ValidateApi(AppleApiOptions options, string? endpoint = null, string? sandboxEndpoint = null)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.KeyId))
            errors.Add($"{nameof(AppleApiOptions.KeyId)} is required.");

        if (string.IsNullOrWhiteSpace(options.IssuerId))
            errors.Add($"{nameof(AppleApiOptions.IssuerId)} is required.");

        if (string.IsNullOrWhiteSpace(options.BundleId))
            errors.Add($"{nameof(AppleApiOptions.BundleId)} is required.");

        if (options.MaxRetries < 1)
            errors.Add($"{nameof(AppleApiOptions.MaxRetries)} must be >= 1 (got {options.MaxRetries}).");

        if (string.IsNullOrWhiteSpace(options.PrivateKeyBase64))
        {
            errors.Add($"{nameof(AppleApiOptions.PrivateKeyBase64)} is required.");
        }
        else
        {
            // Verify the key is base64-decodable and importable as PKCS#8 ECDSA. We do the
            // import here once, at startup, so a typo or wrong key format raises a clear error
            // long before the first verifier call goes near Apple.
            try
            {
                var keyBytes = Convert.FromBase64String(options.PrivateKeyBase64);
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportPkcs8PrivateKey(keyBytes, out _);
                if (ecdsa.KeySize != 256)
                    errors.Add(
                        $"{nameof(AppleApiOptions.PrivateKeyBase64)} must be a P-256 (256-bit) ECDSA key for ES256 signing (got {ecdsa.KeySize}-bit).");
            }
            catch (FormatException ex)
            {
                errors.Add($"{nameof(AppleApiOptions.PrivateKeyBase64)} is not valid base64: {ex.Message}");
            }
            catch (CryptographicException ex)
            {
                errors.Add(
                    $"{nameof(AppleApiOptions.PrivateKeyBase64)} could not be imported as a PKCS#8 ECDSA key. " +
                    $"Strip the '-----BEGIN/END PRIVATE KEY-----' headers and base64-encode the body only. ({ex.Message})");
            }
        }

        if (endpoint is not null)
            ValidateUrl(endpoint, "Endpoint base URL", errors);

        if (sandboxEndpoint is not null)
            ValidateUrl(sandboxEndpoint, "Sandbox fallback base URL (disable SandboxFallback to leave it empty)", errors);

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateUrl(string url, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(url))
            errors.Add($"{label} is required.");
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            errors.Add($"{label} must be an absolute http(s) URI (got '{url}').");
    }
}

internal sealed class AppleSubscriptionOptionsValidator : IValidateOptions<AppleSubscriptionOptions>
{
    public ValidateOptionsResult Validate(string? name, AppleSubscriptionOptions options) =>
        AppleOptionsValidator.ValidateApi(options, options.SubscriptionsBaseUrl,
            options.SandboxFallback ? options.SandboxSubscriptionsBaseUrl : null);
}

internal sealed class AppleInAppPurchaseOptionsValidator : IValidateOptions<AppleInAppPurchaseOptions>
{
    public ValidateOptionsResult Validate(string? name, AppleInAppPurchaseOptions options) =>
        AppleOptionsValidator.ValidateApi(options, options.TransactionsBaseUrl,
            options.SandboxFallback ? options.SandboxTransactionsBaseUrl : null);
}

internal sealed class AppleWebhookAuthenticatorOptionsValidator : IValidateOptions<AppleWebhookAuthenticatorOptions>
{
    public ValidateOptionsResult Validate(string? name, AppleWebhookAuthenticatorOptions options) =>
        string.IsNullOrWhiteSpace(options.BundleId)
            ? ValidateOptionsResult.Fail(
                $"{nameof(AppleWebhookAuthenticatorOptions.BundleId)} is required: notifications signed by Apple " +
                "for other apps must be rejected.")
            : ValidateOptionsResult.Success;
}
