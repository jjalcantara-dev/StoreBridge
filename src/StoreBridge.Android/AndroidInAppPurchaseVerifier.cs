using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoreBridge.Android.Internal;
using StoreBridge.Internal;
using Google.Apis.AndroidPublisher.v3.Data;
using System.Net;

namespace StoreBridge.Android;

/// <summary>
/// Verifies Google Play one-time in-app purchases using the Android Publisher API v3
/// <c>purchases.productsv2</c> endpoint (supports multiple purchase options, offers and partial refunds).
/// </summary>
public sealed class AndroidInAppPurchaseVerifier : IInAppPurchaseVerifier, IDisposable
{
    private readonly AndroidInAppPurchaseOptions _options;
    private readonly ILogger<AndroidInAppPurchaseVerifier> _logger;
    private readonly Lock _clientLock = new();
    private IAndroidProducts? _client;
    private AndroidPublisherService? _ownedService;
    private bool _disposed;

    /// <inheritdoc />
    public Store Store => Store.Android;

    /// <summary>
    /// Creates a new verifier using the provided options.
    /// When <paramref name="service"/> is <c>null</c>, an internal <see cref="AndroidPublisherService"/> is created
    /// lazily and disposed by <see cref="Dispose"/>.
    /// </summary>
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
        if (service != null)
            _client = new GoogleProductsAdapter(service);
    }

    /// <summary>DI-friendly constructor that resolves options from <see cref="IOptions{TOptions}"/>.</summary>
    internal AndroidInAppPurchaseVerifier(
        IOptions<AndroidInAppPurchaseOptions> options,
        ILogger<AndroidInAppPurchaseVerifier>? logger)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), service: null, logger) { }

    internal AndroidInAppPurchaseVerifier(AndroidInAppPurchaseOptions options, IAndroidProducts client)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = NullLogger<AndroidInAppPurchaseVerifier>.Instance;
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    private IAndroidProducts GetClient()
    {
        var existing = Volatile.Read(ref _client);
        if (existing is not null)
            return existing;

        lock (_clientLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is not null)
                return _client;

            var service = AndroidPublisherFactory.Create(_options.CredentialsBase64);
            _ownedService = service;
            var adapter = new GoogleProductsAdapter(service);
            Volatile.Write(ref _client, adapter);
            return adapter;
        }
    }

    /// <summary>Disposes the internal <see cref="AndroidPublisherService"/> if one was created by this instance.</summary>
    public void Dispose()
    {
        AndroidPublisherService? toDispose;
        lock (_clientLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            toDispose = _ownedService;
            _ownedService = null;
        }

        toDispose?.Dispose();
    }

    /// <summary>
    /// Verifies a one-time in-app purchase by purchase token using
    /// <c>purchases.productsv2.getproductpurchasev2</c>.
    /// </summary>
    /// <param name="purchaseToken">The purchase token from the Google Play Billing client.</param>
    /// <param name="productId">
    /// Optional but recommended: the product the client claims to have bought (e.g. "coins_100"). When set,
    /// the purchase must contain a line item for it, otherwise the result is a failure.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<InAppPurchaseVerificationResult> VerifyPurchaseAsync(
        string purchaseToken,
        string? productId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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
    /// <param name="productId">
    /// Optional: the expected product ID. When set, the purchase must contain a line item for it.
    /// </param>
    /// <param name="purchaseToken">The purchase token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<InAppPurchaseVerificationResult> VerifyAsync(
        string packageName,
        string? productId,
        string purchaseToken,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(packageName))
            return InAppPurchaseVerificationResult.Failure(Store, "packageName is required.");
        if (string.IsNullOrWhiteSpace(purchaseToken))
            return InAppPurchaseVerificationResult.Failure(Store, "purchaseToken is required.");

        try
        {
            var client = GetClient();
            var purchase = await AndroidRetryHelper.ExecuteAsync(
                ct => client.GetAsync(packageName, purchaseToken, ct),
                _options.MaxRetries,
                cancellationToken);

            return MapToResult(purchase, productId, purchaseToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Gone)
        {
            _logger.LogInformation("Google purchase {PurchaseToken} returned 410 Gone (expired or unknown)", LogRedaction.Token(purchaseToken));
            return InAppPurchaseVerificationResult.Failure(Store,
                $"Purchase not found or has expired: {ex.Message}");
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex, "Google purchase verification failed for token {PurchaseToken} ({StatusCode})",
                LogRedaction.Token(purchaseToken), ex.HttpStatusCode);
            return InAppPurchaseVerificationResult.Failure(Store,
                $"Google API error {(int)ex.HttpStatusCode}: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Google purchase verification: network error for token {PurchaseToken}", LogRedaction.Token(purchaseToken));
            return InAppPurchaseVerificationResult.Failure(Store, $"Google API network error: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Google purchase verification timed out for token {PurchaseToken}", LogRedaction.Token(purchaseToken));
            return InAppPurchaseVerificationResult.Failure(Store, ex.Message);
        }
        catch (TokenResponseException ex)
        {
            // OAuth token exchange failed: key revoked/deleted, clock skew, or service account disabled
            _logger.LogError(ex, "Google OAuth token request failed — check the service account key in AndroidOptions.CredentialsBase64");
            return InAppPurchaseVerificationResult.Failure(Store, $"Google credentials error: {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException
                                       or ArgumentException or FormatException
                                       or System.Text.Json.JsonException or Newtonsoft.Json.JsonException)
        {
            // Malformed base64 or a JSON that is not a service-account key (only when constructed without DI validation)
            _logger.LogError(ex, "Google credentials could not be loaded — check AndroidOptions.CredentialsBase64");
            return InAppPurchaseVerificationResult.Failure(Store, $"Google credentials error: {ex.Message}");
        }
    }

    private InAppPurchaseVerificationResult MapToResult(ProductPurchaseV2 purchase, string? productId, string purchaseToken)
    {
        var lineItems = purchase.ProductLineItem ?? [];

        ProductLineItem? lineItem;
        if (string.IsNullOrEmpty(productId))
        {
            lineItem = lineItems.FirstOrDefault();
            if (lineItem == null)
                return InAppPurchaseVerificationResult.Failure(Store, "Purchase contains no product line items.");
        }
        else
        {
            // Never accept a token for a different product than the one being unlocked.
            lineItem = lineItems.FirstOrDefault(li => string.Equals(li.ProductId, productId, StringComparison.Ordinal));
            if (lineItem == null)
            {
                var found = string.Join(", ", lineItems.Select(li => li.ProductId));
                return InAppPurchaseVerificationResult.Failure(Store,
                    $"Purchase token does not contain product '{productId}' (found: {(found.Length == 0 ? "none" : found)}).");
            }
        }

        var offer = lineItem.ProductOfferDetails;
        var quantity = offer?.Quantity ?? 1;
        var refundable = offer?.RefundableQuantity;

        return new InAppPurchaseVerificationResult
        {
            IsVerified = true,
            Store = Store,
            Status = MapStatus(purchase.PurchaseStateContext?.PurchaseState, offer?.ConsumptionState, quantity, refundable),
            PurchaseId = purchaseToken,
            ProductId = lineItem.ProductId ?? productId ?? string.Empty,
            OrderId = purchase.OrderId ?? string.Empty,
            PurchasedAt = !string.IsNullOrEmpty(purchase.PurchaseCompletionTimeRaw)
                ? purchase.PurchaseCompletionTimeDateTimeOffset
                : null,
            Quantity = quantity,
            RefundableQuantity = refundable,
            IsAcknowledged = purchase.AcknowledgementState == "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED",
            IsSandbox = purchase.TestPurchaseContext != null
        };
    }

    // https://developers.google.com/android-publisher/api-ref/rest/v3/purchases.productsv2
    private static PurchaseStatus MapStatus(string? purchaseState, string? consumptionState, int quantity, int? refundableQuantity)
    {
        return purchaseState switch
        {
            // refundableQuantity is the quantity not refunded yet: 0 means everything was refunded
            "PURCHASED" when refundableQuantity == 0 && quantity > 0 => PurchaseStatus.Refunded,
            "PURCHASED" when consumptionState == "CONSUMPTION_STATE_CONSUMED" => PurchaseStatus.Consumed,
            "PURCHASED" => PurchaseStatus.Purchased,
            "CANCELLED" => PurchaseStatus.Cancelled,
            "PENDING" => PurchaseStatus.Pending,
            _ => PurchaseStatus.Unknown
        };
    }
}
