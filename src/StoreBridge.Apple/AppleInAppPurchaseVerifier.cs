using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoreBridge.Internal;
using StoreBridge.Apple.Internal;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace StoreBridge.Apple;

/// <summary>
/// Verifies one-time in-app purchases using the App Store Server API v1 transactions endpoint.
/// </summary>
public sealed class AppleInAppPurchaseVerifier : IInAppPurchaseVerifier, IDisposable
{
    /// <summary>Name of the named <see cref="HttpClient"/> registered by DI extensions.</summary>
    public const string HttpClientName = "StoreBridge.Apple.Transactions";

    private readonly AppleInAppPurchaseOptions _options;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<AppleInAppPurchaseVerifier> _logger;
    private readonly AppleTokenCache _tokenCache = new();
    private readonly Lazy<X509Certificate2>? _trustedRoot;
    private bool _disposed;

    /// <inheritdoc />
    public Store Store => Store.Apple;

    /// <summary>
    /// Creates a verifier with options and an optional <see cref="HttpClient"/>.
    /// When <paramref name="httpClient"/> is <c>null</c>, an internal client is created and disposed by <see cref="Dispose"/>.
    /// </summary>
    public AppleInAppPurchaseVerifier(
        AppleInAppPurchaseOptions options,
        HttpClient? httpClient = null,
        ILogger<AppleInAppPurchaseVerifier>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AppleInAppPurchaseVerifier>.Instance;
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient { Timeout = options.RequestTimeout };
        if (options.VerifyResponseSignatures)
            _trustedRoot = new Lazy<X509Certificate2>(() => X509CertificateLoader.LoadCertificate(AppleWebhookAuthenticator.AppleRootCaG3Der));
    }

    /// <summary>DI-friendly constructor that resolves an <see cref="HttpClient"/> from <see cref="IHttpClientFactory"/>.</summary>
    internal AppleInAppPurchaseVerifier(
        IOptions<AppleInAppPurchaseOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<AppleInAppPurchaseVerifier>? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? NullLogger<AppleInAppPurchaseVerifier>.Instance;
        _ownsHttpClient = false;
        if (_options.VerifyResponseSignatures)
            _trustedRoot = new Lazy<X509Certificate2>(() => X509CertificateLoader.LoadCertificate(AppleWebhookAuthenticator.AppleRootCaG3Der));
    }

    private HttpClient ResolveClient() => _httpClientFactory?.CreateClient(HttpClientName) ?? _httpClient!;

    /// <summary>
    /// Verifies a one-time purchase by its transaction ID.
    /// Retries up to <see cref="AppleApiOptions.MaxRetries"/> times on transient HTTP errors and, when
    /// <see cref="AppleApiOptions.SandboxFallback"/> is on, retries a production 404 against the sandbox.
    /// The bearer JWT is cached and reused across requests until 5 minutes before expiry.
    /// </summary>
    /// <param name="purchaseToken">The transaction ID from the App Store.</param>
    /// <param name="productId">Optional. If provided, validates that the returned transaction matches this product.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<InAppPurchaseVerificationResult> VerifyPurchaseAsync(
        string purchaseToken,
        string? productId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(purchaseToken))
            return InAppPurchaseVerificationResult.Failure(Store, "transactionId is required.");

        string jwtToken;
        try
        {
            jwtToken = _tokenCache.GetOrCreate(_options);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Apple JWT generation failed — check configured credentials");
            return InAppPurchaseVerificationResult.Failure(Store, ex.Message);
        }

        string content;
        try
        {
            content = await AppleApiClient.GetAsync(
                ResolveClient(),
                jwtToken,
                _options.TransactionsBaseUrl,
                _options.SandboxFallback ? _options.SandboxTransactionsBaseUrl : null,
                purchaseToken,
                _options.MaxRetries,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Apple in-app purchase verification failed for transaction {TransactionId}", LogRedaction.Token(purchaseToken));
            return InAppPurchaseVerificationResult.Failure(Store, $"Apple API error: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Apple in-app purchase verification timed out for transaction {TransactionId}", LogRedaction.Token(purchaseToken));
            return InAppPurchaseVerificationResult.Failure(Store, ex.Message);
        }

        try
        {
            return MapToResult(content, purchaseToken, productId);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or WebhookAuthenticationException)
        {
            _logger.LogWarning(ex, "Apple in-app purchase verification: response body could not be parsed or verified for {TransactionId}", LogRedaction.Token(purchaseToken));
            return InAppPurchaseVerificationResult.Failure(Store, $"Apple API returned a malformed or unverified response: {ex.Message}");
        }
    }

    /// <summary>Disposes the internal <see cref="HttpClient"/> and root certificate if created by this instance.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_ownsHttpClient)
            _httpClient?.Dispose();
        if (_trustedRoot?.IsValueCreated == true)
            _trustedRoot.Value.Dispose();
    }

    private InAppPurchaseVerificationResult MapToResult(string content, string transactionId, string? productId)
    {
        var parsed = JsonSerializer.Deserialize<AppStoreTransactionResponse>(content);

        if (string.IsNullOrEmpty(parsed?.SignedTransactionInfo))
            return InAppPurchaseVerificationResult.Failure(Store, "No transaction info found in Apple response.");

        var info = AppleJwtHelper.DecodePayload<DecodedTransactionInfo>(parsed.SignedTransactionInfo);
        if (info == null)
            return InAppPurchaseVerificationResult.Failure(Store, "Failed to decode Apple transaction JWT.");

        if (_trustedRoot is not null)
        {
            var roots = new[] { _trustedRoot.Value };
            var txTime = AppleJwtHelper.FromUnixTimeMilliseconds(info.SignedDate)?.UtcDateTime ?? DateTime.UtcNow;
            AppleCertificateChainValidator.ValidateSignature(parsed.SignedTransactionInfo, roots, txTime);
        }

        if (!string.IsNullOrEmpty(info.BundleId) &&
            !string.IsNullOrEmpty(_options.BundleId) &&
            !string.Equals(info.BundleId, _options.BundleId, StringComparison.Ordinal))
        {
            return InAppPurchaseVerificationResult.Failure(Store,
                $"Transaction bundle '{info.BundleId}' does not match configured bundle '{_options.BundleId}'.");
        }

        if (productId != null && !string.Equals(info.ProductId, productId, StringComparison.Ordinal))
            return InAppPurchaseVerificationResult.Failure(Store,
                $"Transaction product '{info.ProductId}' does not match expected '{productId}'.");

        // A set revocationDate means the purchase was refunded or revoked
        var status = info.RevocationDate.HasValue ? PurchaseStatus.Refunded : PurchaseStatus.Purchased;

        var priceAmount = info.Price ?? 0;

        return new InAppPurchaseVerificationResult
        {
            IsVerified = true,
            Store = Store,
            Status = status,
            PurchaseId = info.TransactionId ?? transactionId,
            ProductId = info.ProductId ?? string.Empty,
            PurchasedAt = AppleJwtHelper.FromUnixTimeMilliseconds(info.PurchaseDate),
            Quantity = info.Quantity ?? 1,
            PriceAmount = priceAmount,
            CurrencyCode = info.Currency ?? string.Empty,
            PriceDecimal = PriceConverter.FromApplePrice(priceAmount),
            IsSandbox = AppleJwtHelper.IsNonProduction(info.Environment)
        };
    }
}
