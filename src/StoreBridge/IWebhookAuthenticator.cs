namespace StoreBridge;

/// <summary>
/// Verifies the cryptographic authenticity of an incoming webhook notification.
/// Authentication (this interface) is intentionally separate from parsing (<see cref="IWebhookParser"/>).
/// </summary>
/// <remarks>
/// Always call <see cref="ValidateAsync"/> before <see cref="IWebhookParser.ParseAsync"/> or
/// <see cref="IInAppPurchaseWebhookParser.ParseAsync"/> to ensure the notification was sent
/// by Apple or Google and has not been tampered with.
/// </remarks>
public interface IWebhookAuthenticator
{
    /// <summary>The store this authenticator validates notifications for.</summary>
    Store Store { get; }

    /// <summary>
    /// Validates the authenticity of a webhook notification.
    /// Throws <see cref="WebhookAuthenticationException"/> if validation fails.
    /// </summary>
    /// <param name="rawBody">The raw webhook request body.</param>
    /// <param name="bearerToken">
    /// For Google: the value of the <c>Authorization</c> HTTP header (e.g. "Bearer eyJ...").
    /// For Apple: not used — the signature is embedded in the signed JWT payload.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ValidateAsync(string rawBody, string? bearerToken = null, CancellationToken cancellationToken = default);
}
