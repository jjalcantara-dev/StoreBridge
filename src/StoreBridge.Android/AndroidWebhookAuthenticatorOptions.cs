namespace StoreBridge.Android;

/// <summary>
/// Configuration for authenticating Google Play Pub/Sub webhook notifications.
/// </summary>
public sealed class AndroidWebhookAuthenticatorOptions
{
    /// <summary>
    /// Required. The exact URL of your webhook endpoint. Must match the OIDC token's audience claim.
    /// Example: "https://api.example.com/webhooks/google"
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;

    /// <summary>
    /// Required. The service account email that signs the OIDC token, i.e. the account configured on your
    /// Pub/Sub push subscription's authentication (Cloud Console → Pub/Sub → Subscriptions → your subscription
    /// → Authentication → Service account). Compared case-insensitively with the token's <c>email</c> claim.
    /// Example: "pubsub-pusher@my-project.iam.gserviceaccount.com"
    /// </summary>
    /// <remarks>
    /// This check is what ties the token to <em>your</em> push subscription: anyone with a Google Cloud
    /// account can mint a Google-signed ID token with an arbitrary audience, so audience alone is not enough.
    /// </remarks>
    public string ExpectedServiceAccountEmail { get; set; } = string.Empty;

    /// <summary>
    /// Optional. When set, the notification's <c>packageName</c> must match (defense in depth when a Pub/Sub
    /// topic is shared by several apps).
    /// </summary>
    public string? ExpectedPackageName { get; set; }
}
