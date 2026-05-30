namespace StoreBridge;

/// <summary>
/// Parses incoming server-to-server notifications from a store.
/// </summary>
public interface IWebhookParser
{
    /// <summary>The store this parser handles.</summary>
    Store Store { get; }

    /// <summary>
    /// Parses the raw webhook body into a normalized <see cref="SubscriptionNotification"/>.
    /// </summary>
    /// <param name="rawBody">
    /// For Apple: the raw JSON body containing the signed payload JWT.
    /// For Google: the raw JSON body from the Pub/Sub push message.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<SubscriptionNotification> ParseAsync(string rawBody, CancellationToken cancellationToken = default);
}
