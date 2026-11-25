using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoreBridge.Internal;
using StoreBridge.Apple.Internal;
using System.Security.Cryptography.X509Certificates;
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
    private readonly AppleTokenCache _tokenCache = new();
    private readonly Lazy<X509Certificate2>? _trustedRoot;
    private bool _disposed;

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
        _httpClient = httpClient ?? new HttpClient { Timeout = options.RequestTimeout };
        if (options.VerifyResponseSignatures)
            _trustedRoot = new Lazy<X509Certificate2>(() => X509CertificateLoader.LoadCertificate(AppleWebhookAuthenticator.AppleRootCaG3Der));
    }

    /// <summary>DI-friendly constructor that resolves an <see cref="HttpClient"/> from <see cref="IHttpClientFactory"/>.</summary>
    internal AppleSubscriptionVerifier(
        IOptions<AppleSubscriptionOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<AppleSubscriptionVerifier>? logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? NullLogger<AppleSubscriptionVerifier>.Instance;
        _ownsHttpClient = false;
        if (_options.VerifyResponseSignatures)
            _trustedRoot = new Lazy<X509Certificate2>(() => X509CertificateLoader.LoadCertificate(AppleWebhookAuthenticator.AppleRootCaG3Der));
    }

    private HttpClient ResolveClient() => _httpClientFactory?.CreateClient(HttpClientName) ?? _httpClient!;

    /// <summary>
    /// Verifies a subscription by its original transaction ID.
    /// Retries up to <see cref="AppleApiOptions.MaxRetries"/> times on transient HTTP errors and, when
    /// <see cref="AppleApiOptions.SandboxFallback"/> is on, retries a production 404 against the sandbox.
    /// The bearer JWT is cached and reused across requests until 5 minutes before expiry.
    /// </summary>
    /// <param name="receiptOrToken">The original transaction ID from the App Store.</param>
    /// <param name="productId">
    /// Optional. If provided, only the transaction for that product ID is considered; when the transaction
    /// belongs to other products only, the result is a failure (no silent fallback to another product).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SubscriptionVerificationResult> VerifySubscriptionAsync(
        string receiptOrToken,
        string? productId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(receiptOrToken))
            return SubscriptionVerificationResult.Failure(Store, "originalTransactionId is required.");

        string jwtToken;
        try
        {
            jwtToken = _tokenCache.GetOrCreate(_options);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Apple JWT generation failed — check configured credentials");
            return SubscriptionVerificationResult.Failure(Store, ex.Message);
        }

        string content;
        try
        {
            content = await AppleApiClient.GetAsync(
                ResolveClient(),
                jwtToken,
                _options.SubscriptionsBaseUrl,
                _options.SandboxFallback ? _options.SandboxSubscriptionsBaseUrl : null,
                receiptOrToken,
                _options.MaxRetries,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Apple subscription verification failed for transaction {TransactionId}", LogRedaction.Token(receiptOrToken));
            return SubscriptionVerificationResult.Failure(Store, $"Apple API error: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Apple subscription verification timed out for transaction {TransactionId}", LogRedaction.Token(receiptOrToken));
            return SubscriptionVerificationResult.Failure(Store, ex.Message);
        }

        try
        {
            return MapToResult(content, receiptOrToken, productId);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or WebhookAuthenticationException)
        {
            _logger.LogWarning(ex, "Apple subscription verification: response body could not be parsed or verified for {TransactionId}", LogRedaction.Token(receiptOrToken));
            return SubscriptionVerificationResult.Failure(Store, $"Apple API returned a malformed or unverified response: {ex.Message}");
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

    private SubscriptionVerificationResult MapToResult(string content, string originalTransactionId, string? productId)
    {
        var parsed = JsonSerializer.Deserialize<AppStoreSubscriptionResponse>(content);

        var allTransactions = parsed?.Data?
            .SelectMany(d => d.LastTransactions ?? [])
            .ToList() ?? [];

        if (allTransactions.Count == 0)
            return SubscriptionVerificationResult.Failure(Store, "No transaction info found in Apple response.");

        // Apple's LastTransactionsItem only has originalTransactionId, status, signedTransactionInfo and
        // signedRenewalInfo — productId lives inside the decoded signedTransactionInfo JWT.
        var decoded = new List<(LastTransaction Item, DecodedTransactionInfo Info)>(allTransactions.Count);
        foreach (var item in allTransactions)
        {
            if (string.IsNullOrEmpty(item.SignedTransactionInfo))
                continue;

            var txInfo = AppleJwtHelper.DecodePayload<DecodedTransactionInfo>(item.SignedTransactionInfo);
            if (txInfo != null)
                decoded.Add((item, txInfo));
        }

        if (decoded.Count == 0)
            return SubscriptionVerificationResult.Failure(Store, "No transaction info found in Apple response.");

        (LastTransaction Item, DecodedTransactionInfo Info) match;
        if (productId == null)
        {
            // GET /inApps/v1/subscriptions/{transactionId} returns all subscription groups for the customer.
            // Prefer the item matching the requested transaction ID before falling back to the first entry.
            var byTx = decoded.FindIndex(x =>
                string.Equals(x.Item.OriginalTransactionId, originalTransactionId, StringComparison.Ordinal) ||
                string.Equals(x.Info.OriginalTransactionId, originalTransactionId, StringComparison.Ordinal) ||
                string.Equals(x.Info.TransactionId, originalTransactionId, StringComparison.Ordinal));
            match = byTx >= 0 ? decoded[byTx] : decoded[0];
        }
        else
        {
            // Never fall back to a different product: a client could otherwise present the token of a
            // cheap subscription while asking to unlock an expensive one.
            var byProduct = decoded.FindIndex(x => string.Equals(x.Info.ProductId, productId, StringComparison.Ordinal));
            if (byProduct < 0)
                return SubscriptionVerificationResult.Failure(Store,
                    $"No subscription for product '{productId}' found for this transaction " +
                    $"(found: {string.Join(", ", decoded.Select(x => x.Info.ProductId))}).");

            match = decoded[byProduct];
        }

        var (lastTransaction, info) = match;

        if (_trustedRoot is not null)
        {
            var roots = new[] { _trustedRoot.Value };
            var txTime = AppleJwtHelper.FromUnixTimeMilliseconds(info.SignedDate)?.UtcDateTime ?? DateTime.UtcNow;
            AppleCertificateChainValidator.ValidateSignature(lastTransaction.SignedTransactionInfo!, roots, txTime);
        }

        if (!string.IsNullOrEmpty(info.BundleId) &&
            !string.IsNullOrEmpty(_options.BundleId) &&
            !string.Equals(info.BundleId, _options.BundleId, StringComparison.Ordinal))
        {
            return SubscriptionVerificationResult.Failure(Store,
                $"Transaction bundle '{info.BundleId}' does not match configured bundle '{_options.BundleId}'.");
        }

        DecodedRenewalInfo? renewalInfo = null;
        if (!string.IsNullOrEmpty(lastTransaction.SignedRenewalInfo))
        {
            renewalInfo = AppleJwtHelper.DecodePayload<DecodedRenewalInfo>(lastTransaction.SignedRenewalInfo);
            if (_trustedRoot is not null)
            {
                var roots = new[] { _trustedRoot.Value };
                var renewalTime = AppleJwtHelper.FromUnixTimeMilliseconds(renewalInfo?.SignedDate)?.UtcDateTime ?? DateTime.UtcNow;
                AppleCertificateChainValidator.ValidateSignature(lastTransaction.SignedRenewalInfo, roots, renewalTime);
            }
        }

        // autoRenewStatus 0 = off (user/App Store disabled), 1 = on
        var autoRenewOff = renewalInfo?.AutoRenewStatus == 0;
        var status = AppleStatusMapper.Map(lastTransaction.Status, autoRenewOff);
        var priceAmount = info.Price ?? 0;

        return new SubscriptionVerificationResult
        {
            IsVerified = true,
            Store = Store,
            Status = status,
            SubscriptionId = info.OriginalTransactionId ?? originalTransactionId,
            ProductId = info.ProductId ?? string.Empty,
            ExpiresAt = AppleJwtHelper.FromUnixTimeMilliseconds(info.ExpiresDate),
            PurchasedAt = AppleJwtHelper.FromUnixTimeMilliseconds(info.PurchaseDate),
            IsPromotional = IsPromotional(info),
            PriceAmount = priceAmount,
            CurrencyCode = info.Currency ?? string.Empty,
            PriceDecimal = PriceConverter.FromApplePrice(priceAmount),
            CancelledByUser = autoRenewOff,
            AutoRenewProductId = renewalInfo?.AutoRenewProductId ?? string.Empty,
            GracePeriodExpiresAt = AppleJwtHelper.FromUnixTimeMilliseconds(renewalInfo?.GracePeriodExpiresDate),
            ExternalAccountId = info.AppAccountToken ?? string.Empty,
            IsSandbox = AppleJwtHelper.IsNonProduction(info.Environment)
                     || AppleJwtHelper.IsNonProduction(parsed?.Environment)
        };
    }

    private static bool IsPromotional(DecodedTransactionInfo t) =>
        !string.IsNullOrEmpty(t.OfferIdentifier)
        || t.OfferType.HasValue
        || !string.IsNullOrEmpty(t.OfferDiscountType);
}
