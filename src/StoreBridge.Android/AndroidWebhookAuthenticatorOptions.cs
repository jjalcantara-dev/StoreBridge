namespace StoreBridge.Android;

/// <summary>
/// Configuration for authenticating Google Play Pub/Sub webhook notifications.
/// </summary>
public sealed class AndroidWebhookAuthenticatorOptions
{
    /// <summary>
    /// The exact URL of your webhook endpoint. Must match the OIDC token's audience claim.
    /// Example: "https://api.example.com/webhooks/google"
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional. Expected service account email in the OIDC token.
    /// If set, the token's <c>email</c> claim must match this value exactly (case-insensitive).
    /// Recommended: set this to the email of the service account configured on your Pub/Sub
    /// push subscription's authentication (Cloud Console → Pub/Sub → Subscriptions → your subscription
    /// → Authentication → Service account). That account signs the OIDC token Pub/Sub sends.
    /// Example: "pubsub-pusher@my-project.iam.gserviceaccount.com"
    /// </summary>
    public string? ExpectedServiceAccountEmail { get; set; }
}
