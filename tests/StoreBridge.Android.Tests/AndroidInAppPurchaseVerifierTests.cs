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

    private static ProductPurchaseV2 PurchasedProduct(
        string purchaseState = "PURCHASED",
        string consumptionState = "CONSUMPTION_STATE_YET_TO_BE_CONSUMED",
        string acknowledgementState = "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED",
        bool testPurchase = false,
        int? quantity = null,
        int? refundableQuantity = null,
        string productId = "coins_100") => new()
    {
        PurchaseStateContext = new PurchaseStateContext { PurchaseState = purchaseState },
        AcknowledgementState = acknowledgementState,
        OrderId = "GPA.1111-2222-3333-44444",
        PurchaseCompletionTimeDateTimeOffset = DateTimeOffset.UtcNow.AddDays(-1),
        TestPurchaseContext = testPurchase ? new TestPurchaseContext { FopType = "TEST" } : null,
        ProductLineItem =
        [
            new ProductLineItem
            {
                ProductId = productId,
                ProductOfferDetails = new ProductOfferDetails
                {
                    ConsumptionState = consumptionState,
                    Quantity = quantity,
                    RefundableQuantity = refundableQuantity
                }
            }
        ]
    };

    private static IAndroidProducts ClientReturning(ProductPurchaseV2 purchase)
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(purchase);
        return client;
    }

    // ── purchase state mapping ──────────────────────────────────

    [Theory]
    [InlineData("PURCHASED", "CONSUMPTION_STATE_YET_TO_BE_CONSUMED", PurchaseStatus.Purchased)]
    [InlineData("PURCHASED", "CONSUMPTION_STATE_CONSUMED", PurchaseStatus.Consumed)]
    [InlineData("CANCELLED", "CONSUMPTION_STATE_YET_TO_BE_CONSUMED", PurchaseStatus.Cancelled)]
    [InlineData("PENDING", "CONSUMPTION_STATE_YET_TO_BE_CONSUMED", PurchaseStatus.Pending)]
    [InlineData("PURCHASE_STATE_UNSPECIFIED", "CONSUMPTION_STATE_UNSPECIFIED", PurchaseStatus.Unknown)]
    public async Task VerifyAsync_MapsPurchaseState(string purchaseState, string consumptionState, PurchaseStatus expected)
    {
        var client = ClientReturning(PurchasedProduct(purchaseState, consumptionState));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.True(result.IsVerified);
        Assert.Equal(Store.Android, result.Store);
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task VerifyAsync_FullyRefunded_ReturnsRefunded()
    {
        var client = ClientReturning(PurchasedProduct(quantity: 3, refundableQuantity: 0));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.Equal(PurchaseStatus.Refunded, result.Status);
        Assert.Equal(0, result.RefundableQuantity);
    }

    [Fact]
    public async Task VerifyAsync_PartiallyRefunded_StaysPurchasedAndSurfacesRefundableQuantity()
    {
        var client = ClientReturning(PurchasedProduct(quantity: 3, refundableQuantity: 1));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.Equal(PurchaseStatus.Purchased, result.Status);
        Assert.Equal(3, result.Quantity);
        Assert.Equal(1, result.RefundableQuantity);
    }

    // ── acknowledgement ─────────────────────────────────────────

    [Theory]
    [InlineData("ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED", true)]
    [InlineData("ACKNOWLEDGEMENT_STATE_PENDING", false)]
    [InlineData(null, false)]
    public async Task VerifyAsync_AcknowledgementState(string? state, bool expected)
    {
        var client = ClientReturning(PurchasedProduct(acknowledgementState: state!));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.Equal(expected, result.IsAcknowledged);
    }

    // ── sandbox detection ───────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyAsync_TestPurchaseContext_SetsSandboxFlag(bool testPurchase)
    {
        var client = ClientReturning(PurchasedProduct(testPurchase: testPurchase));

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.Equal(testPurchase, result.IsSandbox);
    }

    // ── quantity ────────────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_WithQuantity_SurfacesQuantity()
    {
        var result = await Build(ClientReturning(PurchasedProduct(quantity: 3)))
            .VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.Equal(3, result.Quantity);
    }

    [Fact]
    public async Task VerifyAsync_NoQuantity_DefaultsToOne()
    {
        var result = await Build(ClientReturning(PurchasedProduct(quantity: null)))
            .VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.Equal(1, result.Quantity);
        Assert.Null(result.RefundableQuantity);
    }

    // ── field extraction & product matching ─────────────────────

    [Fact]
    public async Task VerifyAsync_ReturnsTokenProductOrderAndTime()
    {
        var result = await Build(ClientReturning(PurchasedProduct()))
            .VerifyAsync("com.example.app", "coins_100", "my-purchase-token");

        Assert.Equal("my-purchase-token", result.PurchaseId);
        Assert.Equal("coins_100", result.ProductId);
        Assert.Equal("GPA.1111-2222-3333-44444", result.OrderId);
        Assert.NotNull(result.PurchasedAt);
    }

    [Fact]
    public async Task VerifyAsync_CallsProductsV2WithoutProductId()
    {
        var client = ClientReturning(PurchasedProduct());

        await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        await client.Received(1).GetAsync("com.example.app", "token-001", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyAsync_ProductIdNotInPurchase_ReturnsFailure()
    {
        // Token is for coins_100; the client claims coins_10000 → must not verify
        var result = await Build(ClientReturning(PurchasedProduct(productId: "coins_100")))
            .VerifyAsync("com.example.app", "coins_10000", "token-001");

        Assert.False(result.IsVerified);
        Assert.Contains("coins_10000", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyPurchaseAsync_WithoutProductId_UsesThePurchasedProduct()
    {
        var result = await Build(ClientReturning(PurchasedProduct(productId: "coins_100")))
            .VerifyPurchaseAsync("token-001", productId: null);

        Assert.True(result.IsVerified);
        Assert.Equal("coins_100", result.ProductId);
    }

    // ── error handling ──────────────────────────────────────────

    [Fact]
    public async Task VerifyAsync_410Gone_ReturnsFailed()
    {
        var client = Substitute.For<IAndroidProducts>();
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
        client.GetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Google.GoogleApiException("androidpublisher", "Bad Request")
            {
                HttpStatusCode = HttpStatusCode.BadRequest
            });

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "token-001");

        Assert.False(result.IsVerified);
        Assert.Contains("400", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyAsync_MissingToken_ReturnsFailureWithoutCallingApi()
    {
        var client = Substitute.For<IAndroidProducts>();

        var result = await Build(client).VerifyAsync("com.example.app", "coins_100", "");

        Assert.False(result.IsVerified);
        await client.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, default);
    }

    // ── VerifyPurchaseAsync (via options) ───────────────────────

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
