using Google.Apis.AndroidPublisher.v3.Data;
using StoreBridge.Android;
using StoreBridge.Android.Internal;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Net;

namespace StoreBridge.Android.Tests;

public sealed class AndroidInAppPurchaseVerifierTests
{
    private static readonly AndroidInAppPurchaseOptions DefaultOptions = new()
    {
        PackageName = "com.example.app"
    };

    private static AndroidInAppPurchaseVerifier Build(IAndroidProducts client)
        => new(DefaultOptions, client);

    private static ProductPurchase PurchasedProduct(
        int purchaseState = 0,
        int consumptionState = 0,
        int acknowledgementState = 1,
        int? purchaseType = null,
        int? quantity = null) => new()
    {
        PurchaseState = purchaseState,
        ConsumptionState = consumptionState,
        AcknowledgementState = acknowledgementState,
        PurchaseTimeMillis = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds(),
        PurchaseType = purchaseType,
        Quantity = quantity
    };

    // ── purchase state mapping ──────────────────────────────────

    [Fact]
    public async Task VerifyAsync_PurchasedNotConsumed_ReturnsPurchased()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PurchasedProduct(purchaseState: 0, consumptionState: 0));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.True(result.IsVerified);
        Assert.Equal(Store.Android, result.Store);
        Assert.Equal(PurchaseStatus.Purchased, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_Consumed_ReturnsConsumed()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PurchasedProduct(purchaseState: 0, consumptionState: 1));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.Equal(PurchaseStatus.Consumed, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_Cancelled_ReturnsCancelled()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PurchasedProduct(purchaseState: 1));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.Equal(PurchaseStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_Pending_ReturnsPending()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PurchasedProduct(purchaseState: 2));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.Equal(PurchaseStatus.Pending, result.Status);
    }

    // ── acknowledgement ─────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_Acknowledged_SetsIsAcknowledged()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PurchasedProduct(acknowledgementState: 1));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.True(result.IsAcknowledged);
    }

    [Fact]
    public async Task VerifyAsync_NotAcknowledged_IsAcknowledgedIsFalse()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PurchasedProduct(acknowledgementState: 0));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.False(result.IsAcknowledged);
    }

    // ── sandbox detection ───────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_PurchaseTypeZero_SetsSandboxFlag()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PurchasedProduct(purchaseType: 0));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.True(result.IsSandbox);
    }

    [Fact]
    public async Task VerifyAsync_PurchaseTypeNull_DoesNotSetSandboxFlag()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PurchasedProduct(purchaseType: null));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.False(result.IsSandbox);
    }

    // ── quantity ────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_WithQuantity_SurfacesQuantity()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PurchasedProduct(quantity: 3));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.Equal(3, result.Quantity);
    }

    [Fact]
    public async Task VerifyAsync_NoQuantity_DefaultsToOne()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PurchasedProduct(quantity: null));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.Equal(1, result.Quantity);
    }

    // ── field extraction ────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_ReturnsPurchaseTokenAndProductId()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(PurchasedProduct());

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "my-purchase-token");

        Assert.Equal("my-purchase-token", result.PurchaseId);
        Assert.Equal("coins_100", result.ProductId);
        Assert.NotNull(result.PurchasedAt);
    }

    // ── error handling ──────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_410Gone_ReturnsFailed()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Google.GoogleApiException("androidpublisher", "Gone")
            {
                HttpStatusCode = HttpStatusCode.Gone
            });

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.False(result.IsVerified);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyAsync_GoogleApiError_ReturnsFailed()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Google.GoogleApiException("androidpublisher", "Bad Request")
            {
                HttpStatusCode = HttpStatusCode.BadRequest
            });

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.False(result.IsVerified);
        Assert.Contains("400", result.ErrorMessage);
    }

    // ── VerifyPurchaseAsync (via options) ───────────────────────

    [Fact]
    public async Task VerifyPurchaseAsync_MissingProductId_ReturnsFailure()
    {
        var client = Substitute.For<IAndroidProducts>();
        var verifier = Build(client);

        var result = await verifier.VerifyPurchaseAsync("token", productId: null);

        Assert.False(result.IsVerified);
        Assert.Contains("productId", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyPurchaseAsync_MissingPackageName_ReturnsFailure()
    {
        var client = Substitute.For<IAndroidProducts>();
        var verifier = new AndroidInAppPurchaseVerifier(
            new AndroidInAppPurchaseOptions { PackageName = "" }, client);

        var result = await verifier.VerifyPurchaseAsync("token", productId: "coins_100");

        Assert.False(result.IsVerified);
        Assert.Contains(nameof(AndroidOptions.PackageName), result.ErrorMessage);
    }
}
