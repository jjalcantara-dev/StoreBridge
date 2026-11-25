using Google.Apis.AndroidPublisher.v3;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.AndroidPublisher.v3.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StoreBridge.Internal;
using StoreBridge.Android.Internal;
using System.Net;

namespace StoreBridge.Android;

/// <summary>
/// Verifies Google Play subscriptions using the Android Publisher API v3 subscriptionsv2 endpoint.
/// </summary>
public sealed class AndroidSubscriptionVerifier : ISubscriptionVerifier, IDisposable
{
    private readonly AndroidSubscriptionOptions _options;
    private readonly ILogger<AndroidSubscriptionVerifier> _logger;
    private readonly Lock _clientLock = new();
    private IAndroidSubscriptionsv2? _client;
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
    public AndroidSubscriptionVerifier(
        AndroidSubscriptionOptions options,
        AndroidPublisherService? service = null,
        ILogger<AndroidSubscriptionVerifier>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<AndroidSubscriptionVerifier>.Instance;
        if (service != null)
            _client = new GoogleSubscriptionsv2Adapter(service);
    }

    /// <summary>DI-friendly constructor that resolves options from <see cref="IOptions{TOptions}"/>.</summary>
    internal AndroidSubscriptionVerifier(
        IOptions<AndroidSubscriptionOptions> options,
        ILogger<AndroidSubscriptionVerifier>? logger)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), service: null, logger) { }

    internal AndroidSubscriptionVerifier(AndroidSubscriptionOptions options, IAndroidSubscriptionsv2 client)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = NullLogger<AndroidSubscriptionVerifier>.Instance;
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    private IAndroidSubscriptionsv2 GetClient()
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
            var adapter = new GoogleSubscriptionsv2Adapter(service);
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
    /// Verifies a subscription by purchase token.
    /// </summary>
    /// <param name="receiptOrToken">The purchase token from the Google Play Billing client.</param>
    /// <param name="productId">
    /// The subscription product ID (e.g. "premium_monthly"). Required.
    /// The purchase token must contain a line item for this product; otherwise the result is a failure.
    /// The package name is taken from <see cref="AndroidOptions.PackageName"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<SubscriptionVerificationResult> VerifySubscriptionAsync(
        string receiptOrToken,
        string? productId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

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
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(packageName))
            return SubscriptionVerificationResult.Failure(Store, "packageName is required.");
        if (string.IsNullOrWhiteSpace(subscriptionId))
            return SubscriptionVerificationResult.Failure(Store, "subscriptionId is required.");
        if (string.IsNullOrWhiteSpace(purchaseToken))
            return SubscriptionVerificationResult.Failure(Store, "purchaseToken is required.");

        try
        {
            var client = GetClient();
            var sub = await AndroidRetryHelper.ExecuteAsync(
                ct => client.GetAsync(packageName, purchaseToken, ct),
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
                LogRedaction.Token(purchaseToken), ex.HttpStatusCode);
            return SubscriptionVerificationResult.Failure(
                Store, $"Google API error {(int)ex.HttpStatusCode}: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Google subscription verification: network error for token {PurchaseToken}", LogRedaction.Token(purchaseToken));
            return SubscriptionVerificationResult.Failure(Store, $"Google API network error: {ex.Message}");
        }
        catch (TimeoutException ex)
        {
            _logger.LogWarning(ex, "Google subscription verification timed out for token {PurchaseToken}", LogRedaction.Token(purchaseToken));
            return SubscriptionVerificationResult.Failure(Store, ex.Message);
        }
        catch (TokenResponseException ex)
        {
            // OAuth token exchange failed: key revoked/deleted, clock skew, or service account disabled
            _logger.LogError(ex, "Google OAuth token request failed — check the service account key in AndroidOptions.CredentialsBase64");
            return SubscriptionVerificationResult.Failure(Store, $"Google credentials error: {ex.Message}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException
                                       or ArgumentException or FormatException
                                       or System.Text.Json.JsonException or Newtonsoft.Json.JsonException)
        {
            // Malformed base64 or a JSON that is not a service-account key (only when constructed without DI validation)
            _logger.LogError(ex, "Google credentials could not be loaded — check AndroidOptions.CredentialsBase64");
            return SubscriptionVerificationResult.Failure(Store, $"Google credentials error: {ex.Message}");
        }
    }

    private SubscriptionVerificationResult MapToResult(
        SubscriptionPurchaseV2 sub, string subscriptionId, string purchaseToken)
    {
        // Only the line item for the requested product counts. Never fall back to another product: a client
        // could otherwise present the token of a cheap subscription while asking to unlock an expensive one.
        var lineItem = sub.LineItems?.FirstOrDefault(li =>
            string.Equals(li.ProductId, subscriptionId, StringComparison.Ordinal));

        if (lineItem == null)
        {
            var found = string.Join(", ", sub.LineItems?.Select(li => li.ProductId) ?? []);
            return SubscriptionVerificationResult.Failure(Store,
                $"Purchase token does not contain subscription '{subscriptionId}' (found: {(found.Length == 0 ? "none" : found)}).");
        }

        var expiresAt = !string.IsNullOrEmpty(lineItem.ExpiryTimeRaw)
            ? lineItem.ExpiryTimeDateTimeOffset
            : (DateTimeOffset?)null;
        var purchasedAt = !string.IsNullOrEmpty(sub.StartTimeRaw)
            ? sub.StartTimeDateTimeOffset
            : (DateTimeOffset?)null;

        // recurringPrice: the plan's list price — excludes taxes and introductory/offer discounts.
        var price = lineItem.AutoRenewingPlan?.RecurringPrice;
        var priceMicros = price is null ? 0 : ToMicros(price.Units, price.Nanos);

        return new SubscriptionVerificationResult
        {
            IsVerified = true,
            Store = Store,
            Status = MapState(sub.SubscriptionState, expiresAt),
            SubscriptionId = purchaseToken,
            ProductId = lineItem.ProductId ?? subscriptionId,
            ExpiresAt = expiresAt,
            PurchasedAt = purchasedAt,
            IsPromotional = !string.IsNullOrEmpty(lineItem.OfferDetails?.OfferId),
            PriceAmount = priceMicros,
            CurrencyCode = price?.CurrencyCode ?? string.Empty,
            PriceDecimal = PriceConverter.FromGoogleMicros(priceMicros),
            CancelledByUser = sub.CanceledStateContext?.UserInitiatedCancellation != null,
            IsSandbox = sub.TestPurchase != null
        };
    }

    private static long ToMicros(long? units, int? nanos) =>
        (units ?? 0) * 1_000_000 + (nanos ?? 0) / 1_000;

    private static SubscriptionStatus MapState(string? state, DateTimeOffset? expiresAt)
    {
        var expired = expiresAt.HasValue && expiresAt.Value < DateTimeOffset.UtcNow;

        // The reported state wins for ON_HOLD / IN_GRACE_PERIOD / PAUSED: in those states the line item's
        // expiryTime is legitimately in the past (hold/pause) or extended (grace), so it must not be used to
        // override them. Expiry only downgrades states that claim the user is still entitled.
        return state switch
        {
            "SUBSCRIPTION_STATE_ACTIVE" => expired ? SubscriptionStatus.Expired : SubscriptionStatus.Active,
            // CANCELED = auto-renew off but still within the paid period
            "SUBSCRIPTION_STATE_CANCELED" => expired ? SubscriptionStatus.Expired : SubscriptionStatus.Cancelled,
            "SUBSCRIPTION_STATE_IN_GRACE_PERIOD" => SubscriptionStatus.InGracePeriod,
            "SUBSCRIPTION_STATE_ON_HOLD" => SubscriptionStatus.InBillingRetry,
            // PAUSED has no normalized equivalent (no access, not expired)
            "SUBSCRIPTION_STATE_PAUSED" => SubscriptionStatus.Unknown,
            "SUBSCRIPTION_STATE_EXPIRED" => SubscriptionStatus.Expired,
            // PENDING = initial purchase not yet paid; PENDING_PURCHASE_CANCELED = that pending purchase was dropped
            "SUBSCRIPTION_STATE_PENDING" => SubscriptionStatus.Unknown,
            "SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED" => SubscriptionStatus.Unknown,
            _ => expired ? SubscriptionStatus.Expired : SubscriptionStatus.Unknown
        };
    }
}
