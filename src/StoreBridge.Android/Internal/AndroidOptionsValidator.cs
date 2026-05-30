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
                // The Google SDK only accepts the JSON service-account format; verify it parses
                // at startup so an invalid key file doesn't surface as a generic Pub/Sub-time error.
                try
                {
                    using var stream = new MemoryStream(bytes);
#pragma warning disable CS0618 // GoogleCredential.FromStream is the only sync entry point; CredentialFactory replacement is async-only
                    _ = GoogleCredential.FromStream(stream);
#pragma warning restore CS0618
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or System.Text.Json.JsonException)
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

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
