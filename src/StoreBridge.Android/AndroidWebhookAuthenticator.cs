using Google.Apis.Auth;
using Microsoft.Extensions.Options;
using StoreBridge.Android.Internal;

namespace StoreBridge.Android;

/// <summary>
/// Verifies the authenticity of Google Play Pub/Sub webhook notifications
/// by validating the OIDC Bearer token that Google includes in the
/// <c>Authorization</c> HTTP header of every push message.
/// </summary>
public sealed class AndroidWebhookAuthenticator : IWebhookAuthenticator
{
    private readonly AndroidWebhookAuthenticatorOptions _options;
    private readonly IGoogleTokenValidator _tokenValidator;

    /// <inheritdoc />
    public Store Store => Store.Android;

    /// <summary>Creates a new authenticator using the provided options.</summary>
    /// <param name="options">Google Pub/Sub authentication configuration.</param>
    public AndroidWebhookAuthenticator(AndroidWebhookAuthenticatorOptions options)
        : this(options, new GoogleTokenValidator()) { }

    /// <summary>DI-friendly constructor that resolves options from <see cref="IOptions{TOptions}"/>.</summary>
    internal AndroidWebhookAuthenticator(IOptions<AndroidWebhookAuthenticatorOptions> options)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), new GoogleTokenValidator()) { }

    internal AndroidWebhookAuthenticator(
        AndroidWebhookAuthenticatorOptions options,
        IGoogleTokenValidator tokenValidator)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(options.WebhookUrl))
            throw new ArgumentException(
                $"{nameof(AndroidWebhookAuthenticatorOptions.WebhookUrl)} must be set.", nameof(options));

        if (string.IsNullOrWhiteSpace(options.ExpectedServiceAccountEmail))
            throw new ArgumentException(
                $"{nameof(AndroidWebhookAuthenticatorOptions.ExpectedServiceAccountEmail)} must be set: without it any " +
                "Google-signed ID token minted for your URL would be accepted.", nameof(options));

        _tokenValidator = tokenValidator ?? throw new ArgumentNullException(nameof(tokenValidator));
    }

    /// <summary>
    /// Validates the OIDC Bearer token from the Google Pub/Sub push request.
    /// </summary>
    /// <param name="rawBody">
    /// The raw webhook body. Only inspected when <see cref="AndroidWebhookAuthenticatorOptions.ExpectedPackageName"/>
    /// is set, to check the notification's <c>packageName</c>.
    /// </param>
    /// <param name="bearerToken">
    /// The value of the <c>Authorization</c> HTTP header.
    /// Accepted formats: <c>"Bearer eyJ..."</c> or the raw JWT string.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="WebhookAuthenticationException">
    /// Thrown when the token is missing, expired, has an invalid signature, wrong audience, an unverified or
    /// mismatched service account email, or when the package name does not match.
    /// </exception>
    public async Task ValidateAsync(
        string rawBody,
        string? bearerToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new WebhookAuthenticationException(
                "Missing Authorization Bearer token. Google Pub/Sub push requests must include an OIDC token " +
                "in the Authorization header. Ensure your Pub/Sub subscription is configured with authentication enabled.");

        var token = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? bearerToken["Bearer ".Length..]
            : bearerToken;

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await _tokenValidator.ValidateAsync(token, _options.WebhookUrl, cancellationToken);
        }
        catch (InvalidJwtException ex)
        {
            throw new WebhookAuthenticationException(
                $"Google Pub/Sub OIDC token validation failed: {ex.Message}", ex);
        }

        if (!payload.EmailVerified ||
            !string.Equals(payload.Email, _options.ExpectedServiceAccountEmail, StringComparison.OrdinalIgnoreCase))
        {
            throw new WebhookAuthenticationException(
                $"Pub/Sub service account email mismatch. " +
                $"Expected verified '{_options.ExpectedServiceAccountEmail}', got '{payload.Email}' (email_verified={payload.EmailVerified}).");
        }

        if (!string.IsNullOrWhiteSpace(_options.ExpectedPackageName))
        {
            var (notification, _) = AndroidPubSubReader.Parse(rawBody);
            if (!string.Equals(notification.PackageName, _options.ExpectedPackageName, StringComparison.Ordinal))
                throw new WebhookAuthenticationException(
                    $"Notification is for package '{notification.PackageName}', expected '{_options.ExpectedPackageName}'.");
        }
    }
}
