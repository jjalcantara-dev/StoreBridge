namespace StoreBridge;

/// <summary>
/// Thrown when a webhook notification fails cryptographic authentication,
/// indicating the request did not originate from Apple or Google.
/// </summary>
public sealed class WebhookAuthenticationException : Exception
{
    /// <inheritdoc />
    public WebhookAuthenticationException(string message) : base(message) { }

    /// <inheritdoc />
    public WebhookAuthenticationException(string message, Exception innerException) : base(message, innerException) { }
}
