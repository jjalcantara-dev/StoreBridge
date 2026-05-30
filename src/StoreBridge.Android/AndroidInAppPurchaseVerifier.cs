using Google.Apis.AndroidPublisher.v3;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoreBridge.Android.Internal;
using System.Net;

namespace StoreBridge.Android;

/// <summary>
/// Verifies Google Play one-time in-app purchases using the Android Publisher API v3.
/// </summary>
public sealed class AndroidInAppPurchaseVerifier : IInAppPurchaseVerifier
{
    private readonly AndroidInAppPurchaseOptions _options;
    private readonly Lazy<IAndroidProducts> _client;
    private readonly ILogger<AndroidInAppPurchaseVerifier> _logger;

    /// <inheritdoc />
    public Store Store => Store.Android;

    /// <summary>Creates a new verifier using the provided options.</summary>
    /// <param name="options">Google Play API configuration.</param>
    /// <param name="service">Optional pre-built service (useful for testing).</param>
    /// <param name="logger">Optional logger; falls back to <see cref="NullLogger{T}"/>.</param>
    public AndroidInAppPurchaseVerifier(
        AndroidInAppPurchaseOptions options,
        AndroidPublisherService? service = null,
        ILogger<AndroidInAppPurchaseVerifier>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AndroidInAppPurchaseVerifier>.Instance;
        _client = service != null
            ? new Lazy<IAndroidProducts>(() => new GoogleProductsAdapter(service))
            : new Lazy<IAndroidProducts>(() => new GoogleProductsAdapter(AndroidPublisherFactory.Create(_options.CredentialsBase64)));
    }

    /// <summary>DI-friendly constructor that resolves options from <see cref="IOptions{TOptions}"/>.</summary>
    internal AndroidInAppPurchaseVerifier(
        IOptions<AndroidInAppPurchaseOptions> options,
        ILogger<AndroidInAppPurchaseVerifier> logger)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), service: null, logger) { }

    internal AndroidInAppPurchaseVerifier(AndroidInAppPurchaseOptions options, IAndroidProducts client)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = NullLogger<AndroidInAppPurchaseVerifier>.Instance;
        _client = new Lazy<IAndroidProducts>(() => client);
    }

    /// <summary>
    /// Verifies a one-time in-app purchase by purchase token.
    /// </summary>
    /// <param name="purchaseToken">The purchase token from the Google Play Billing client.</param>
    /// <param name="productId">The product SKU (e.g. "coins_100"). Required.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<InAppPurchaseVerificationResult> VerifyPurchaseAsync(
        string purchaseToken,
        string? productId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(productId))
            return Task.FromResult(InAppPurchaseVerificationResult.Failure(
                Store, "productId is required for Google Play in-app purchase verification."));

        if (string.IsNullOrEmpty(_options.PackageName))
            return Task.FromResult(InAppPurchaseVerificationResult.Failure(
                Store, $"{nameof(AndroidOptions.PackageName)} must be set in options."));

        return VerifyAsync(_options.PackageName, productId, purchaseToken, cancellationToken);
    }

    /// <summary>
    /// Verifies a purchase with an explicit package name, for multi-app scenarios.
    /// Retries up to <see cref="AndroidOptions.MaxRetries"/> times on transient errors.
    /// </summary>
    /// <param name="packageName">The app package name.</param>
    /// <param name="productId">The product SKU.</param>
    /// <param name="purchaseToken">The purchase token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<InAppPurchaseVerificationResult> VerifyAsync(
        string packageName,
        string productId,
        string purchaseToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(packageName))
            return InAppPurchaseVerificationResult.Failure(Store, "packageName is required.");
        if (string.IsNullOrWhiteSpace(productId))
            return InAppPurchaseVerificationResult.Failure(Store, "productId is required.");
        if (string.IsNullOrWhiteSpace(purchaseToken))
            return InAppPurchaseVerificationResult.Failure(Store, "purchaseToken is required.");

        try
        {
            var product = await AndroidRetryHelper.ExecuteAsync(
                ct => _client.Value.GetAsync(packageName, productId, purchaseToken, ct),
                _options.MaxRetries,
                cancellationToken);

            var status = (product.PurchaseState, product.ConsumptionState) switch
            {
                (0, 1) => PurchaseStatus.Consumed,
                (0, _) => PurchaseStatus.Purchased,
                (1, _) => PurchaseStatus.Cancelled,
                (2, _) => PurchaseStatus.Pending,
                _ => PurchaseStatus.Unknown
            };

            return new InAppPurchaseVerificationResult
            {
                IsVerified = true,
                Store = Store,
                Status = status,
                PurchaseId = purchaseToken,
                ProductId = productId,
                PurchasedAt = product.PurchaseTimeMillis.HasValue
                    ? DateTimeOffset.FromUnixTimeMilliseconds(product.PurchaseTimeMillis.Value)
                    : null,
                Quantity = product.Quantity ?? 1,
                IsAcknowledged = product.AcknowledgementState == 1,
                IsSandbox = product.PurchaseType == 0
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Gone)
        {
            _logger.LogInformation("Google purchase {PurchaseToken} returned 410 Gone (expired or unknown)", purchaseToken);
            return InAppPurchaseVerificationResult.Failure(Store,
                $"Purchase not found or has expired: {ex.Message}");
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex, "Google purchase verification failed for token {PurchaseToken} ({StatusCode})",
                purchaseToken, ex.HttpStatusCode);
            return InAppPurchaseVerificationResult.Failure(Store,
                $"Google API error {(int)ex.HttpStatusCode}: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Google purchase verification: network error for token {PurchaseToken}", purchaseToken);
            return InAppPurchaseVerificationResult.Failure(Store, $"Google API network error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Google credentials could not be loaded — check AndroidOptions.CredentialsBase64");
            return InAppPurchaseVerificationResult.Failure(Store, $"Google credentials error: {ex.Message}");
        }
    }
}
