using StoreBridge.Apple;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace StoreBridge.Apple.Tests;

public sealed class AppleInAppPurchaseVerifierTests
{
    // ── helpers ────────────────────────────────────────────────

    private static AppleInAppPurchaseOptions BuildOptions(int maxRetries = 1) => new()
    {
        KeyId = "TESTKEY001",
        IssuerId = "00000000-0000-0000-0000-000000000000",
        BundleId = "com.example.app",
        PrivateKeyBase64 = CreateEc256Key(),
        MaxRetries = maxRetries
    };

    private static string CreateEc256Key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportPkcs8PrivateKey());
    }

    private static string Jwt(object payload)
    {
        var header = Enc("""{"alg":"ES256"}""");
        var body = Enc(JsonSerializer.Serialize(payload));
        return $"{header}.{body}.fakesig";
    }

    private static string Enc(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Builds the Apple transactions-API response JSON.</summary>
    private static string BuildResponse(
        string productId = "coins_100",
        long? purchaseDate = null,
        long? revocationDate = null,
        string environment = "Production",
        int? quantity = null)
    {
        purchaseDate ??= DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();

        var txFields = new Dictionary<string, object?>
        {
            ["transactionId"] = "tx-iap-001",
            ["productId"] = productId,
            ["purchaseDate"] = purchaseDate,
            ["price"] = 4990L,
            ["currency"] = "USD",
            ["environment"] = environment
        };

        if (revocationDate.HasValue)
            txFields["revocationDate"] = revocationDate.Value;

        if (quantity.HasValue)
            txFields["quantity"] = quantity.Value;

        return JsonSerializer.Serialize(new { signedTransactionInfo = Jwt(txFields) });
    }

    // ── Bug #1: revocation detection ───────────────────────────

    [Fact]
    public async Task VerifyPurchaseAsync_WithRevocationDate_ReturnsRefunded()
    {
        // revocationDate present → purchase was refunded/revoked
        var body = BuildResponse(revocationDate: DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds());
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, body));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("token-001", "coins_100");

        Assert.True(result.IsVerified);
        Assert.Equal(PurchaseStatus.Refunded, result.Status);
    }

    [Fact]
    public async Task VerifyPurchaseAsync_WithoutRevocationDate_ReturnsPurchased()
    {
        var body = BuildResponse();
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, body));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("token-001", "coins_100");

        Assert.True(result.IsVerified);
        Assert.Equal(PurchaseStatus.Purchased, result.Status);
    }

    // ── basic verification ──────────────────────────────────────

    [Fact]
    public async Task VerifyPurchaseAsync_ValidPurchase_ReturnsVerified()
    {
        var body = BuildResponse();
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, body));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("token-001", "coins_100");

        Assert.True(result.IsVerified);
        Assert.Equal(Store.Apple, result.Store);
        Assert.Equal("coins_100", result.ProductId);
        Assert.NotNull(result.PurchasedAt);
    }

    [Fact]
    public async Task VerifyPurchaseAsync_ProductIdMismatch_ReturnsFailed()
    {
        // Response contains "coins_100" but we expect "gems_50"
        var body = BuildResponse(productId: "coins_100");
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, body));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("token-001", productId: "gems_50");

        Assert.False(result.IsVerified);
        Assert.Contains("coins_100", result.ErrorMessage);
        Assert.Contains("gems_50", result.ErrorMessage);
    }

    // ── sandbox detection ───────────────────────────────────────

    [Fact]
    public async Task VerifyPurchaseAsync_SandboxEnvironment_SetsSandboxFlag()
    {
        var body = BuildResponse(environment: "Sandbox");
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, body));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("token-001", "coins_100");

        Assert.True(result.IsSandbox);
    }

    // ── quantity ────────────────────────────────────────────────

    [Fact]
    public async Task VerifyPurchaseAsync_WithQuantity_SurfacesQuantity()
    {
        var body = BuildResponse(quantity: 5);
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, body));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("token-001", "coins_100");

        Assert.Equal(5, result.Quantity);
    }

    [Fact]
    public async Task VerifyPurchaseAsync_NoQuantity_DefaultsToOne()
    {
        var body = BuildResponse();
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, body));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("token-001", "coins_100");

        Assert.Equal(1, result.Quantity);
    }

    // ── price conversion ────────────────────────────────────────

    [Fact]
    public async Task VerifyPurchaseAsync_PriceIsConvertedFromThousandths()
    {
        var body = BuildResponse();
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, body));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("token-001", "coins_100");

        Assert.Equal(4990L, result.PriceAmount);
        Assert.Equal(4.990m, result.PriceDecimal);
        Assert.Equal("USD", result.CurrencyCode);
    }

    // ── error handling ──────────────────────────────────────────

    [Fact]
    public async Task VerifyPurchaseAsync_NonSuccessResponse_ReturnsFailed()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.NotFound, """{"errorCode":4290000}"""));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("token-001", "coins_100");

        Assert.False(result.IsVerified);
        Assert.Contains("404", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifyPurchaseAsync_EmptySignedTransactionInfo_ReturnsFailed()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, """{}"""));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("token-001", "coins_100");

        Assert.False(result.IsVerified);
    }

    // ── retry behavior ──────────────────────────────────────────

    [Fact]
    public async Task VerifyPurchaseAsync_4xxResponse_DoesNotRetry()
    {
        var handler = new FakeHttpMessageHandler(
            (HttpStatusCode.NotFound, "{}"),
            (HttpStatusCode.NotFound, "{}"),
            (HttpStatusCode.NotFound, "{}"));
        var options = BuildOptions(maxRetries: 3);
        using var verifier = new AppleInAppPurchaseVerifier(options, new HttpClient(handler));

        await verifier.VerifyPurchaseAsync("token", "coins_100");

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task VerifyPurchaseAsync_5xxThenSuccess_Retries()
    {
        var handler = new FakeHttpMessageHandler(
            (HttpStatusCode.ServiceUnavailable, "unavailable"),
            (HttpStatusCode.OK, BuildResponse()));
        var options = BuildOptions(maxRetries: 2);
        using var verifier = new AppleInAppPurchaseVerifier(options, new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("token", "coins_100");

        Assert.Equal(2, handler.CallCount);
        Assert.True(result.IsVerified);
    }
}
