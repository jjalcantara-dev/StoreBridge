using Google.Apis.AndroidPublisher.v3.Data;
using StoreBridge.Android;
using StoreBridge.Android.Internal;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Net;

namespace StoreBridge.Android.Tests;

public sealed class AndroidSubscriptionVerifierTests
{
    private static readonly AndroidSubscriptionOptions DefaultOptions = new()
    {
        PackageName = "com.example.app"
    };

    private static AndroidSubscriptionVerifier Build(IAndroidSubscriptionsv2 client)
        => new(DefaultOptions, client);

    // Sets ExpiryTimeDateTimeOffset (which auto-populates ExpiryTimeRaw via Google's formatter)
    private static SubscriptionPurchaseLineItem LineItem(string productId, DateTimeOffset expiry) => new()
    {
        ProductId = productId,
        ExpiryTimeDateTimeOffset = expiry
    };

    private static SubscriptionPurchaseV2 ActiveSub(string productId = "premium_monthly") => new()
    {
        SubscriptionState = "SUBSCRIPTION_STATE_ACTIVE",
        LineItems = [LineItem(productId, DateTimeOffset.UtcNow.AddMonths(1))],
        StartTimeDateTimeOffset = DateTimeOffset.UtcNow.AddMonths(-1)
    };

    // ── state mapping ───────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_ActiveState_ReturnsActive()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ActiveSub());

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.True(result.IsVerified);
        Assert.Equal(Store.Android, result.Store);
        Assert.Equal(SubscriptionStatus.Active, result.Status);
        Assert.Equal("premium_monthly", result.ProductId);
        Assert.NotNull(result.ExpiresAt);
        Assert.NotNull(result.PurchasedAt);
    }

    [Fact]
    public async Task VerifyAsync_CanceledState_ReturnsCancelledAndStillGrantsAccess()
    {
        // CANCELED = auto-renew off but still within the paid billing period
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SubscriptionPurchaseV2
            {
                SubscriptionState = "SUBSCRIPTION_STATE_CANCELED",
                CanceledStateContext = new CanceledStateContext { UserInitiatedCancellation = new UserInitiatedCancellation() },
                LineItems = [LineItem("premium_monthly", DateTimeOffset.UtcNow.AddDays(10))]
            });

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.Equal(SubscriptionStatus.Cancelled, result.Status);
        Assert.True(result.Status.GrantsAccess());
        Assert.True(result.CancelledByUser);
    }

    [Fact]
    public async Task VerifyAsync_SystemCancelled_IsNotCancelledByUser()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SubscriptionPurchaseV2
            {
                SubscriptionState = "SUBSCRIPTION_STATE_CANCELED",
                CanceledStateContext = new CanceledStateContext { SystemInitiatedCancellation = new SystemInitiatedCancellation() },
                LineItems = [LineItem("premium_monthly", DateTimeOffset.UtcNow.AddDays(10))]
            });

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.Equal(SubscriptionStatus.Cancelled, result.Status);
        Assert.False(result.CancelledByUser);
    }

    [Theory]
    [InlineData("SUBSCRIPTION_STATE_ON_HOLD", SubscriptionStatus.InBillingRetry)]
    [InlineData("SUBSCRIPTION_STATE_PAUSED", SubscriptionStatus.Unknown)]
    [InlineData("SUBSCRIPTION_STATE_IN_GRACE_PERIOD", SubscriptionStatus.InGracePeriod)]
    public async Task VerifyAsync_StateWinsOverPastExpiry(string state, SubscriptionStatus expected)
    {
        // During account hold / pause the line item's expiryTime is already in the past
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SubscriptionPurchaseV2
            {
                SubscriptionState = state,
                LineItems = [LineItem("premium_monthly", DateTimeOffset.UtcNow.AddDays(-3))]
            });

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_ProductNotInLineItems_FailsInsteadOfFallingBack()
    {
        // Token is for premium_monthly; the client claims premium_yearly → must not verify
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ActiveSub("premium_monthly"));

        var result = await Build(client).VerifyAsync("com.example.app", "premium_yearly", "token-001");

        Assert.False(result.IsVerified);
        Assert.Contains("premium_yearly", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_RecurringPrice_IsMappedToMicros()
    {
        var sub = ActiveSub();
        sub.LineItems[0].AutoRenewingPlan = new AutoRenewingPlan
        {
            AutoRenewEnabled = true,
            RecurringPrice = new Money { CurrencyCode = "EUR", Units = 4, Nanos = 990_000_000 }
        };
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(sub);

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.Equal(4_990_000, result.PriceAmount);
        Assert.Equal(4.99m, result.PriceDecimal);
        Assert.Equal("EUR", result.CurrencyCode);
    }

    [Fact]
    public async Task VerifyAsync_OAuthTokenFailure_ReturnsFailure()
    {
        // e.g. the service-account key was deleted → invalid_grant during the token exchange
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Google.Apis.Auth.OAuth2.Responses.TokenResponseException(
                new Google.Apis.Auth.OAuth2.Responses.TokenErrorResponse { Error = "invalid_grant" }));

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.False(result.IsVerified);
        Assert.Contains("credentials", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_HttpTimeout_ReturnsFailure()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException("timeout", new TimeoutException()));

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.False(result.IsVerified);
        Assert.Contains("timed out", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_InvalidBase64CredentialsWithoutDi_ReturnsFailure()
    {
        var verifier = new AndroidSubscriptionVerifier(new AndroidSubscriptionOptions
        {
            PackageName = "com.example.app",
            CredentialsBase64 = "not base64!!"
        });

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.False(result.IsVerified);
        Assert.Contains("credentials", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_ExpiredState_ReturnsExpired()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SubscriptionPurchaseV2
            {
                SubscriptionState = "SUBSCRIPTION_STATE_EXPIRED",
                LineItems = [new SubscriptionPurchaseLineItem { ProductId = "premium_monthly" }]
            });

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.Equal(SubscriptionStatus.Expired, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_InGracePeriodState_ReturnsInGracePeriod()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SubscriptionPurchaseV2
            {
                SubscriptionState = "SUBSCRIPTION_STATE_IN_GRACE_PERIOD",
                LineItems = [LineItem("premium_monthly", DateTimeOffset.UtcNow.AddDays(3))]
            });

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.Equal(SubscriptionStatus.InGracePeriod, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_OnHoldState_ReturnsInBillingRetry()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SubscriptionPurchaseV2
            {
                SubscriptionState = "SUBSCRIPTION_STATE_ON_HOLD",
                LineItems = [new SubscriptionPurchaseLineItem { ProductId = "premium_monthly" }]
            });

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.Equal(SubscriptionStatus.InBillingRetry, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_PastExpiryDate_ReturnsExpiredRegardlessOfState()
    {
        // Expiry in the past → Expired even if state says Active
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SubscriptionPurchaseV2
            {
                SubscriptionState = "SUBSCRIPTION_STATE_ACTIVE",
                LineItems = [LineItem("premium_monthly", DateTimeOffset.UtcNow.AddDays(-1))]
            });

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.Equal(SubscriptionStatus.Expired, result.Status);
    }

    // ── CancelledByUser via UserInitiatedCancellation ───────────

    [Fact]
    public async Task VerifyAsync_UserInitiatedCancellation_SetsCancelledByUser()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SubscriptionPurchaseV2
            {
                SubscriptionState = "SUBSCRIPTION_STATE_ACTIVE",
                CanceledStateContext = new CanceledStateContext
                {
                    UserInitiatedCancellation = new UserInitiatedCancellation()
                },
                LineItems = [LineItem("premium_monthly", DateTimeOffset.UtcNow.AddDays(5))]
            });

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.True(result.CancelledByUser);
    }

    // ── sandbox detection ───────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_TestPurchase_SetsSandboxFlag()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SubscriptionPurchaseV2
            {
                SubscriptionState = "SUBSCRIPTION_STATE_ACTIVE",
                TestPurchase = new TestPurchase(),
                LineItems = [LineItem("premium_monthly", DateTimeOffset.UtcNow.AddMonths(1))]
            });

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.True(result.IsSandbox);
    }

    [Fact]
    public async Task VerifyAsync_NoTestPurchase_DoesNotSetSandboxFlag()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ActiveSub());

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.False(result.IsSandbox);
    }

    // ── promotional offer ────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_OfferIdPresent_SetsIsPromotional()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SubscriptionPurchaseV2
            {
                SubscriptionState = "SUBSCRIPTION_STATE_ACTIVE",
                LineItems =
                [
                    new SubscriptionPurchaseLineItem
                    {
                        ProductId = "premium_monthly",
                        ExpiryTimeDateTimeOffset = DateTimeOffset.UtcNow.AddMonths(1),
                        OfferDetails = new() { OfferId = "offer-free-trial" }
                    }
                ]
            });

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.True(result.IsPromotional);
    }

    [Fact]
    public async Task VerifyAsync_NoOfferDetails_IsPromotionalIsFalse()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ActiveSub());

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.False(result.IsPromotional);
    }

    // ── error handling ──────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_410Gone_ReturnsExpiredResult()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Google.GoogleApiException("androidpublisher", "Gone")
            {
                HttpStatusCode = HttpStatusCode.Gone
            });

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.True(result.IsVerified);
        Assert.Equal(SubscriptionStatus.Expired, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_GoogleApiError_ReturnsFailed()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Google.GoogleApiException("androidpublisher", "Forbidden")
            {
                HttpStatusCode = HttpStatusCode.Forbidden
            });

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.False(result.IsVerified);
        Assert.Contains("403", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_5xx_RetriesAndEventuallySucceeds()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new Google.GoogleApiException("androidpublisher", "boom") { HttpStatusCode = HttpStatusCode.InternalServerError },
                _ => Task.FromResult(ActiveSub()));

        var options = new AndroidSubscriptionOptions { PackageName = "com.example.app", MaxRetries = 2 };
        var verifier = new AndroidSubscriptionVerifier(options, client);

        var result = await verifier.VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.True(result.IsVerified);
        await client.Received(2).GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyAsync_429_RetriesThenFailsCleanly()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Google.GoogleApiException("androidpublisher", "rate-limited")
            {
                HttpStatusCode = (HttpStatusCode)429
            });

        var options = new AndroidSubscriptionOptions { PackageName = "com.example.app", MaxRetries = 2 };
        var verifier = new AndroidSubscriptionVerifier(options, client);

        var result = await verifier.VerifyAsync("com.example.app", "premium_monthly", "token-001");

        Assert.False(result.IsVerified);
        await client.Received(2).GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyAsync_4xx_DoesNotRetry()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Google.GoogleApiException("androidpublisher", "Forbidden")
            {
                HttpStatusCode = HttpStatusCode.Forbidden
            });

        var options = new AndroidSubscriptionOptions { PackageName = "com.example.app", MaxRetries = 3 };
        var verifier = new AndroidSubscriptionVerifier(options, client);

        await verifier.VerifyAsync("com.example.app", "premium_monthly", "token-001");

        await client.Received(1).GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyAsync_EmptyPurchaseToken_ReturnsFailureWithoutCallingApi()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();

        var result = await Build(client).VerifyAsync("com.example.app", "premium_monthly", "");

        Assert.False(result.IsVerified);
        await client.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyAsync_Cancelled_PropagatesCancellation()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ActiveSub());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Build(client).VerifyAsync("com.example.app", "premium_monthly", "token-001", cts.Token));
    }

    // ── VerifySubscriptionAsync (via options) ───────────────────

    [Fact]
    public async Task VerifySubscriptionAsync_MissingProductId_ReturnsFailure()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        var verifier = Build(client);

        var result = await verifier.VerifySubscriptionAsync("token", productId: null);

        Assert.False(result.IsVerified);
        Assert.Contains("productId", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifySubscriptionAsync_MissingPackageName_ReturnsFailure()
    {
        var client = Substitute.For<IAndroidSubscriptionsv2>();
        var verifier = new AndroidSubscriptionVerifier(new AndroidSubscriptionOptions { PackageName = "" }, client);

        var result = await verifier.VerifySubscriptionAsync("token", productId: "premium_monthly");

        Assert.False(result.IsVerified);
        Assert.Contains(nameof(AndroidOptions.PackageName), result.ErrorMessage);
    }
}
