namespace StoreBridge;

/// <summary>
/// Parses incoming server-to-server in-app purchase notifications from a store.
/// </summary>
public interface IInAppPurchaseWebhookParser
{
    /// <summary>The store this parser handles.</summary>
    Store Store { get; }

    /// <summary>
    /// Parses the raw webhook body into a normalized <see cref="InAppPurchaseNotification"/>.
    /// </summary>
    /// <param name="rawBody">
    /// For Apple: the raw JSON body containing the signed payload JWT.
    /// For Google: the raw JSON body from the Pub/Sub push message.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<InAppPurchaseNotification> ParseAsync(string rawBody, CancellationToken cancellationToken = default);
}
