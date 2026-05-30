using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoreBridge.Apple.Internal;
using System.Net.Http.Headers;
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
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>DI-friendly constructor that resolves an <see cref="HttpClient"/> from <see cref="IHttpClientFactory"/>.</summary>
    internal AppleInAppPurchaseVerifier(
        IOptions<AppleInAppPurchaseOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<AppleInAppPurchaseVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? NullLogger<AppleInAppPurchaseVerifier>.Instance;
        _ownsHttpClient = false;
    }

    private HttpClient ResolveClient() => _httpClientFactory?.CreateClient(HttpClientName) ?? _httpClient!;

    /// <summary>
    /// Verifies a one-time purchase by its transaction ID.
    /// Retries up to <see cref="AppleApiOptions.MaxRetries"/> times on transient HTTP errors.
    /// The JWT is generated once and reused across retries.
    /// </summary>
    /// <param name="purchaseToken">The transaction ID from the App Store.</param>
    /// <param name="productId">Optional. If provided, validates that the returned transaction matches this product.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<InAppPurchaseVerificationResult> VerifyPurchaseAsync(
        string purchaseToken,
        string? productId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(purchaseToken))
            return InAppPurchaseVerificationResult.Failure(Store, "transactionId is required.");

        string jwtToken;
        try
        {
            jwtToken = AppleJwtHelper.GenerateToken(
                _options.KeyId, _options.IssuerId, _options.BundleId, _options.PrivateKeyBase64);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Apple JWT generation failed — check configured credentials");
            return InAppPurchaseVerificationResult.Failure(Store, ex.Message);
        }

        try
        {
            return await AppleRetryHelper.ExecuteAsync(
                ct => VerifyInternalAsync(jwtToken, purchaseToken, productId, ct),
                _options.MaxRetries,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Apple in-app purchase verification failed for transaction {TransactionId}", purchaseToken);
            return InAppPurchaseVerificationResult.Failure(Store, $"Apple API error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Apple in-app purchase verification: response body could not be parsed for {TransactionId}", purchaseToken);
            return InAppPurchaseVerificationResult.Failure(Store, $"Apple API returned a malformed response: {ex.Message}");
        }
    }

    /// <summary>Disposes the internal <see cref="HttpClient"/> if one was created by this instance.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient?.Dispose();
    }

    private async Task<InAppPurchaseVerificationResult> VerifyInternalAsync(
        string jwtToken,
        string transactionId,
        string? productId,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.TransactionsBaseUrl.TrimEnd('/')}/{transactionId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwtToken);

        var response = await ResolveClient().SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Apple API returned {(int)response.StatusCode}: {body}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsed = JsonSerializer.Deserialize<AppStoreTransactionResponse>(content);

        if (string.IsNullOrEmpty(parsed?.SignedTransactionInfo))
            return InAppPurchaseVerificationResult.Failure(Store, "No transaction info found in Apple response.");

        var info = AppleJwtHelper.DecodePayload<DecodedTransactionInfo>(parsed.SignedTransactionInfo);
        if (info == null)
            return InAppPurchaseVerificationResult.Failure(Store, "Failed to decode Apple transaction JWT.");

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
            PurchasedAt = info.PurchaseDate.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(info.PurchaseDate.Value)
                : null,
            Quantity = info.Quantity ?? 1,
            PriceAmount = priceAmount,
            CurrencyCode = info.Currency ?? string.Empty,
            PriceDecimal = PriceConverter.FromApplePrice(priceAmount),
            IsSandbox = string.Equals(info.Environment, "Sandbox", StringComparison.OrdinalIgnoreCase)
        };
    }
}
