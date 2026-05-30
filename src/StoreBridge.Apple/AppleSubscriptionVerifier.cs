using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoreBridge.Apple.Internal;
using System.Net.Http.Headers;
using System.Text.Json;

namespace StoreBridge.Apple;

/// <summary>
/// Verifies Apple App Store subscriptions using the App Store Server API v1.
/// </summary>
public sealed class AppleSubscriptionVerifier : ISubscriptionVerifier, IDisposable
{
    /// <summary>Name of the named <see cref="HttpClient"/> registered by DI extensions.</summary>
    public const string HttpClientName = "StoreBridge.Apple.Subscriptions";

    private readonly AppleSubscriptionOptions _options;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly HttpClient? _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger<AppleSubscriptionVerifier> _logger;

    /// <inheritdoc />
    public Store Store => Store.Apple;

    /// <summary>
    /// Creates a verifier with options and an optional <see cref="HttpClient"/>.
    /// When <paramref name="httpClient"/> is <c>null</c>, an internal client is created and disposed by <see cref="Dispose"/>.
    /// </summary>
    public AppleSubscriptionVerifier(
        AppleSubscriptionOptions options,
        HttpClient? httpClient = null,
        ILogger<AppleSubscriptionVerifier>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AppleSubscriptionVerifier>.Instance;
        _ownsHttpClient = httpClient == null;
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>DI-friendly constructor that resolves an <see cref="HttpClient"/> from <see cref="IHttpClientFactory"/>.</summary>
    internal AppleSubscriptionVerifier(
        IOptions<AppleSubscriptionOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<AppleSubscriptionVerifier> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? NullLogger<AppleSubscriptionVerifier>.Instance;
        _ownsHttpClient = false;
    }

    private HttpClient ResolveClient() => _httpClientFactory?.CreateClient(HttpClientName) ?? _httpClient!;

    /// <summary>
    /// Verifies a subscription by its original transaction ID.
    /// Retries up to <see cref="AppleApiOptions.MaxRetries"/> times on transient HTTP errors.
    /// The JWT is generated once and reused across retries.
    /// </summary>
    /// <param name="receiptOrToken">The original transaction ID from the App Store.</param>
    /// <param name="productId">Optional. If provided, only the transaction matching that product ID is returned.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SubscriptionVerificationResult> VerifySubscriptionAsync(
        string receiptOrToken,
        string? productId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(receiptOrToken))
            return SubscriptionVerificationResult.Failure(Store, "originalTransactionId is required.");

        string jwtToken;
        try
        {
            // Generate once — the token is valid for 20 minutes; all retries share it.
            jwtToken = AppleJwtHelper.GenerateToken(
                _options.KeyId, _options.IssuerId, _options.BundleId, _options.PrivateKeyBase64);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Apple JWT generation failed — check configured credentials");
            return SubscriptionVerificationResult.Failure(Store, ex.Message);
        }

        try
        {
            return await AppleRetryHelper.ExecuteAsync(
                ct => VerifyInternalAsync(jwtToken, receiptOrToken, productId, ct),
                _options.MaxRetries,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Apple subscription verification failed for transaction {TransactionId}", receiptOrToken);
            return SubscriptionVerificationResult.Failure(Store, $"Apple API error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Apple subscription verification: response body could not be parsed for {TransactionId}", receiptOrToken);
            return SubscriptionVerificationResult.Failure(Store, $"Apple API returned a malformed response: {ex.Message}");
        }
    }

    /// <summary>Disposes the internal <see cref="HttpClient"/> if one was created by this instance.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient?.Dispose();
    }

    private async Task<SubscriptionVerificationResult> VerifyInternalAsync(
        string jwtToken,
        string originalTransactionId,
        string? productId,
        CancellationToken cancellationToken)
    {
        var url = $"{_options.SubscriptionsBaseUrl.TrimEnd('/')}/{originalTransactionId}";
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
        var parsed = JsonSerializer.Deserialize<AppStoreSubscriptionResponse>(content);

        var allTransactions = parsed?.Data?
            .SelectMany(d => d.LastTransactions ?? [])
            .ToList() ?? [];

        var lastTransaction = productId == null
            ? allTransactions.FirstOrDefault()
            : allTransactions.FirstOrDefault(t => string.Equals(t.ProductId, productId, StringComparison.Ordinal))
              ?? allTransactions.FirstOrDefault();

        if (lastTransaction?.SignedTransactionInfo == null)
            return SubscriptionVerificationResult.Failure(Store, "No transaction info found in Apple response.");

        var info = AppleJwtHelper.DecodePayload<DecodedTransactionInfo>(lastTransaction.SignedTransactionInfo);
        if (info == null)
            return SubscriptionVerificationResult.Failure(Store, "Failed to decode Apple transaction JWT.");

        DecodedRenewalInfo? renewalInfo = null;
        if (!string.IsNullOrEmpty(lastTransaction.SignedRenewalInfo))
            renewalInfo = AppleJwtHelper.DecodePayload<DecodedRenewalInfo>(lastTransaction.SignedRenewalInfo);

        var status = AppleStatusMapper.Map(lastTransaction.Status);
        var priceAmount = info.Price ?? 0;

        return new SubscriptionVerificationResult
        {
            IsVerified = true,
            Store = Store,
            Status = status,
            SubscriptionId = info.OriginalTransactionId ?? originalTransactionId,
            ProductId = info.ProductId ?? string.Empty,
            ExpiresAt = info.ExpiresDate.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(info.ExpiresDate.Value)
                : null,
            PurchasedAt = info.PurchaseDate.HasValue
                ? DateTimeOffset.FromUnixTimeMilliseconds(info.PurchaseDate.Value)
                : null,
            IsPromotional = IsPromotional(info),
            PriceAmount = priceAmount,
            CurrencyCode = info.Currency ?? string.Empty,
            PriceDecimal = PriceConverter.FromApplePrice(priceAmount),
            // autoRenewStatus 0 = off (user disabled), 1 = on
            CancelledByUser = renewalInfo?.AutoRenewStatus == 0,
            AutoRenewProductId = renewalInfo?.AutoRenewProductId ?? string.Empty,
            GracePeriodExpiresAt = renewalInfo?.GracePeriodExpiresDate.HasValue == true
                ? DateTimeOffset.FromUnixTimeMilliseconds(renewalInfo.GracePeriodExpiresDate!.Value)
                : null,
            IsSandbox = string.Equals(info.Environment, "Sandbox", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(parsed?.Environment, "Sandbox", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static bool IsPromotional(DecodedTransactionInfo t) =>
        !string.IsNullOrEmpty(t.OfferIdentifier)
        || t.OfferType.HasValue
        || !string.IsNullOrEmpty(t.OfferDiscountType);
}
