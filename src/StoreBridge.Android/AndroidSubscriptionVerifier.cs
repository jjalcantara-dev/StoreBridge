using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoreBridge.Android.Internal;
using System.Net;

namespace StoreBridge.Android;

/// <summary>
/// Verifies Google Play subscriptions using the Android Publisher API v3 subscriptionsv2 endpoint.
/// </summary>
public sealed class AndroidSubscriptionVerifier : ISubscriptionVerifier
{
    private readonly AndroidSubscriptionOptions _options;
    private readonly Lazy<IAndroidSubscriptionsv2> _client;
    private readonly ILogger<AndroidSubscriptionVerifier> _logger;

    /// <inheritdoc />
    public Store Store => Store.Android;

    /// <summary>Creates a new verifier using the provided options.</summary>
    /// <param name="options">Google Play API configuration.</param>
    /// <param name="service">Optional pre-built service (useful for testing).</param>
    /// <param name="logger">Optional logger; falls back to <see cref="NullLogger{T}"/>.</param>
    public AndroidSubscriptionVerifier(
        AndroidSubscriptionOptions options,
        AndroidPublisherService? service = null,
        ILogger<AndroidSubscriptionVerifier>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AndroidSubscriptionVerifier>.Instance;
        _client = service != null
            ? new Lazy<IAndroidSubscriptionsv2>(() => new GoogleSubscriptionsv2Adapter(service))
            : new Lazy<IAndroidSubscriptionsv2>(() => new GoogleSubscriptionsv2Adapter(AndroidPublisherFactory.Create(_options.CredentialsBase64)));
    }

    /// <summary>DI-friendly constructor that resolves options from <see cref="IOptions{TOptions}"/>.</summary>
    internal AndroidSubscriptionVerifier(
        IOptions<AndroidSubscriptionOptions> options,
        ILogger<AndroidSubscriptionVerifier> logger)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), service: null, logger) { }

    internal AndroidSubscriptionVerifier(AndroidSubscriptionOptions options, IAndroidSubscriptionsv2 client)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = NullLogger<AndroidSubscriptionVerifier>.Instance;
        _client = new Lazy<IAndroidSubscriptionsv2>(() => client);
    }

    /// <summary>
    /// Verifies a subscription by purchase token.
    /// </summary>
    /// <param name="receiptOrToken">The purchase token from the Google Play Billing client.</param>
    /// <param name="productId">
    /// The subscription product ID (e.g. "premium_monthly"). Required.
    /// Used to select the matching line item from the V2 response.
    /// The package name is taken from <see cref="AndroidOptions.PackageName"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<SubscriptionVerificationResult> VerifySubscriptionAsync(
        string receiptOrToken,
        string? productId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(productId))
            return Task.FromResult(SubscriptionVerificationResult.Failure(
                Store, "productId (subscription ID) is required for Google Play verification."));

        if (string.IsNullOrEmpty(_options.PackageName))
            return Task.FromResult(SubscriptionVerificationResult.Failure(
                Store, $"{nameof(AndroidOptions.PackageName)} must be set in options."));

        return VerifyAsync(_options.PackageName, productId, receiptOrToken, cancellationToken);
    }

    /// <summary>
    /// Verifies a subscription with an explicit package name, for multi-app scenarios.
    /// Retries up to <see cref="AndroidOptions.MaxRetries"/> times on transient errors.
    /// </summary>
    /// <param name="packageName">The app package name.</param>
    /// <param name="subscriptionId">The subscription product ID, used to select the matching line item.</param>
    /// <param name="purchaseToken">The purchase token.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<SubscriptionVerificationResult> VerifyAsync(
        string packageName,
        string subscriptionId,
        string purchaseToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(packageName))
            return SubscriptionVerificationResult.Failure(Store, "packageName is required.");
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return SubscriptionVerificationResult.Failure(Store, "subscriptionId is required.");
        if (string.IsNullOrWhiteSpace(purchaseToken))
            return SubscriptionVerificationResult.Failure(Store, "purchaseToken is required.");

        try
        {
            var sub = await AndroidRetryHelper.ExecuteAsync(
                ct => _client.Value.GetAsync(packageName, purchaseToken, ct),
                _options.MaxRetries,
                cancellationToken);
            return MapToResult(sub, subscriptionId, purchaseToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.Gone)
        {
            // Google returns 410 Gone for subscriptions expired 60+ days ago
            return new SubscriptionVerificationResult
            {
                IsVerified = true,
                Store = Store,
                Status = SubscriptionStatus.Expired,
                SubscriptionId = purchaseToken,
                ProductId = subscriptionId
            };
        }
        catch (Google.GoogleApiException ex)
        {
            _logger.LogWarning(ex, "Google subscription verification failed for token {PurchaseToken} ({StatusCode})",
                purchaseToken, ex.HttpStatusCode);
            return SubscriptionVerificationResult.Failure(
                Store, $"Google API error {(int)ex.HttpStatusCode}: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Google subscription verification: network error for token {PurchaseToken}", purchaseToken);
            return SubscriptionVerificationResult.Failure(Store, $"Google API network error: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            // GoogleCredential.FromStream throws this when the JSON credentials are malformed
            _logger.LogError(ex, "Google credentials could not be loaded — check AndroidOptions.CredentialsBase64");
            return SubscriptionVerificationResult.Failure(Store, $"Google credentials error: {ex.Message}");
        }
    }

    private SubscriptionVerificationResult MapToResult(
        SubscriptionPurchaseV2 sub, string subscriptionId, string purchaseToken)
    {
        // Prefer the line item whose productId matches; fall back to the first one
        var lineItem = sub.LineItems?.FirstOrDefault(li =>
            string.Equals(li.ProductId, subscriptionId, StringComparison.OrdinalIgnoreCase))
            ?? sub.LineItems?.FirstOrDefault();

        var expiresAt = lineItem != null && !string.IsNullOrEmpty(lineItem.ExpiryTimeRaw)
            ? lineItem.ExpiryTimeDateTimeOffset
            : (DateTimeOffset?)null;
        var purchasedAt = !string.IsNullOrEmpty(sub.StartTimeRaw)
            ? sub.StartTimeDateTimeOffset
            : (DateTimeOffset?)null;
        var state = sub.SubscriptionState;

        var status = MapState(state, expiresAt);
        var cancelledByUser =
            sub.CanceledStateContext?.UserInitiatedCancellation != null
            || state == "SUBSCRIPTION_STATE_CANCELED";

        return new SubscriptionVerificationResult
        {
            IsVerified = true,
            Store = Store,
            Status = status,
            SubscriptionId = purchaseToken,
            ProductId = lineItem?.ProductId ?? subscriptionId,
            ExpiresAt = expiresAt,
            PurchasedAt = purchasedAt,
            IsPromotional = !string.IsNullOrEmpty(lineItem?.OfferDetails?.OfferId),
            // V2 does not include price in the purchase response; use the catalog API if needed
            PriceAmount = 0,
            CurrencyCode = string.Empty,
            PriceDecimal = 0,
            CancelledByUser = cancelledByUser,
            IsSandbox = sub.TestPurchase != null
        };
    }

    private static SubscriptionStatus MapState(string? state, DateTimeOffset? expiresAt)
    {
        // Treat an already-passed expiry as Expired regardless of reported state
        if (expiresAt.HasValue && expiresAt.Value < DateTimeOffset.UtcNow)
            return SubscriptionStatus.Expired;

        return state switch
        {
            "SUBSCRIPTION_STATE_ACTIVE" => SubscriptionStatus.Active,
            // CANCELED = user cancelled but still within the paid period
            "SUBSCRIPTION_STATE_CANCELED" => SubscriptionStatus.Active,
            "SUBSCRIPTION_STATE_IN_GRACE_PERIOD" => SubscriptionStatus.InGracePeriod,
            "SUBSCRIPTION_STATE_ON_HOLD" => SubscriptionStatus.InBillingRetry,
            // PAUSED has no direct equivalent; surface as Unknown
            "SUBSCRIPTION_STATE_PAUSED" => SubscriptionStatus.Unknown,
            "SUBSCRIPTION_STATE_EXPIRED" => SubscriptionStatus.Expired,
            // PENDING = initial purchase not yet confirmed by the store
            "SUBSCRIPTION_STATE_PENDING" => SubscriptionStatus.Unknown,
            _ => SubscriptionStatus.Unknown
        };
    }
}
