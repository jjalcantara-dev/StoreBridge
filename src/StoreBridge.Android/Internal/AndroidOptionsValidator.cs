using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace StoreBridge.Android.Internal;

/// <summary>
/// Runs at the first <see cref="IOptions{TOptions}.Value"/> resolution so misconfigured Google
/// credentials (missing package name, garbled base64, JSON that isn't a service-account key)
/// fail fast instead of producing opaque Google.Apis errors on the first verifier call.
/// </summary>
internal static class AndroidOptionsValidator
{
    internal static ValidateOptionsResult ValidateApi(AndroidOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.PackageName))
            errors.Add($"{nameof(AndroidOptions.PackageName)} is required (e.g. \"com.example.app\").");

        if (options.MaxRetries < 1)
            errors.Add($"{nameof(AndroidOptions.MaxRetries)} must be >= 1 (got {options.MaxRetries}).");

        if (string.IsNullOrWhiteSpace(options.CredentialsBase64))
        {
            errors.Add($"{nameof(AndroidOptions.CredentialsBase64)} is required.");
        }
        else
        {
            byte[]? bytes = null;
            try
            {
                bytes = Convert.FromBase64String(options.CredentialsBase64);
            }
            catch (FormatException ex)
            {
                errors.Add($"{nameof(AndroidOptions.CredentialsBase64)} is not valid base64: {ex.Message}");
            }

            if (bytes is not null)
            {
                // Only service-account keys are accepted (CredentialFactory rejects other credential types such as
                // external_account configs, which can point at arbitrary files/URLs). Verify it parses at startup
                // so an invalid key file doesn't surface as a generic error on the first verifier call.
                try
                {
                    using var stream = new MemoryStream(bytes);
                    _ = CredentialFactory.FromStream<ServiceAccountCredential>(stream);
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException
                                               or ArgumentException or FormatException
                                               or System.Text.Json.JsonException or Newtonsoft.Json.JsonException)
                {
                    errors.Add(
                        $"{nameof(AndroidOptions.CredentialsBase64)} is not a valid Google service-account JSON " +
                        $"key. Re-export the JSON key from Cloud Console and base64-encode the whole file. ({ex.Message})");
                }
            }
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class AndroidSubscriptionOptionsValidator : IValidateOptions<AndroidSubscriptionOptions>
{
    public ValidateOptionsResult Validate(string? name, AndroidSubscriptionOptions options) =>
        AndroidOptionsValidator.ValidateApi(options);
}

internal sealed class AndroidInAppPurchaseOptionsValidator : IValidateOptions<AndroidInAppPurchaseOptions>
{
    public ValidateOptionsResult Validate(string? name, AndroidInAppPurchaseOptions options) =>
        AndroidOptionsValidator.ValidateApi(options);
}

internal sealed class AndroidWebhookAuthenticatorOptionsValidator : IValidateOptions<AndroidWebhookAuthenticatorOptions>
{
    public ValidateOptionsResult Validate(string? name, AndroidWebhookAuthenticatorOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.WebhookUrl))
        {
            errors.Add(
                $"{nameof(AndroidWebhookAuthenticatorOptions.WebhookUrl)} is required. " +
                "Set it to the absolute https URL of your webhook endpoint — it MUST match the " +
                "OIDC token's audience claim that Google Pub/Sub sends.");
        }
        else if (!Uri.TryCreate(options.WebhookUrl, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add(
                $"{nameof(AndroidWebhookAuthenticatorOptions.WebhookUrl)} must be an absolute http(s) URI " +
                $"(got '{options.WebhookUrl}').");
        }

        if (string.IsNullOrWhiteSpace(options.ExpectedServiceAccountEmail))
        {
            errors.Add(
                $"{nameof(AndroidWebhookAuthenticatorOptions.ExpectedServiceAccountEmail)} is required. Set it to the " +
                "service account configured on your Pub/Sub push subscription's authentication — without it any " +
                "Google-signed ID token minted for your URL would be accepted.");
        }
        else if (!options.ExpectedServiceAccountEmail.Contains('@'))
        {
            errors.Add(
                $"{nameof(AndroidWebhookAuthenticatorOptions.ExpectedServiceAccountEmail)} must be an email address " +
                $"(got '{options.ExpectedServiceAccountEmail}').");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
