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
            (HttpStatusCode.Unauthorized, "{}"),
            (HttpStatusCode.Unauthorized, "{}"),
            (HttpStatusCode.Unauthorized, "{}"));
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

    // ── sandbox fallback ────────────────────────────────────────

    [Fact]
    public async Task VerifyPurchaseAsync_ProductionNotFound_FallsBackToSandbox()
    {
        var handler = new FakeHttpMessageHandler(
            (HttpStatusCode.NotFound, """{"errorCode":4040010,"errorMessage":"Transaction id not found."}"""),
            (HttpStatusCode.OK, BuildResponse()));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("2000000000000001", "coins_100");

        Assert.True(result.IsVerified);
        Assert.Equal(2, handler.CallCount);
        Assert.StartsWith("https://api.storekit.itunes.apple.com/", handler.RequestUris[0].ToString());
        Assert.StartsWith("https://api.storekit-sandbox.itunes.apple.com/", handler.RequestUris[1].ToString());
    }

    [Fact]
    public async Task VerifyPurchaseAsync_SandboxFallbackDisabled_DoesNotRetrySandbox()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.NotFound, "{}"));
        var options = BuildOptions();
        options.SandboxFallback = false;
        using var verifier = new AppleInAppPurchaseVerifier(options, new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("2000000000000001", "coins_100");

        Assert.False(result.IsVerified);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task VerifyPurchaseAsync_TransactionIdIsEscapedInPath()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, BuildResponse()));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(), new HttpClient(handler));

        await verifier.VerifyPurchaseAsync("../../v2/history/1", "coins_100");

        Assert.Equal("/inApps/v1/transactions/..%2F..%2Fv2%2Fhistory%2F1", handler.RequestUris[0].AbsolutePath);
    }

    // ── transient failures ──────────────────────────────────────

    [Fact]
    public async Task VerifyPurchaseAsync_429ThenSuccess_Retries()
    {
        var handler = new FakeHttpMessageHandler(
            (HttpStatusCode.TooManyRequests, "{}"),
            (HttpStatusCode.OK, BuildResponse()));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(maxRetries: 2), new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("token", "coins_100");

        Assert.True(result.IsVerified);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task VerifyPurchaseAsync_HttpClientTimeout_ReturnsFailureInsteadOfThrowing()
    {
        // HttpClient.Timeout surfaces as TaskCanceledException even though the caller did not cancel
        var handler = new FakeHttpMessageHandler(
            ((HttpStatusCode)0, "", (Exception?)new TaskCanceledException("timeout", new TimeoutException())));
        using var verifier = new AppleInAppPurchaseVerifier(BuildOptions(maxRetries: 1), new HttpClient(handler));

        var result = await verifier.VerifyPurchaseAsync("token", "coins_100");

        Assert.False(result.IsVerified);
        Assert.Contains("timed out", result.ErrorMessage);
    }

    // ── bearer token ────────────────────────────────────────────

    [Fact]
    public async Task VerifyPurchaseAsync_SendsValidEs256BearerToken()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var options = BuildOptions();
        options.PrivateKeyBase64 = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, BuildResponse()));
        using var verifier = new AppleInAppPurchaseVerifier(options, new HttpClient(handler));

        await verifier.VerifyPurchaseAsync("token", "coins_100");

        var parts = handler.LastBearerToken!.Split('.');
        Assert.Equal(3, parts.Length);
        var valid = key.VerifyData(System.Text.Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            System.Buffers.Text.Base64Url.DecodeFromChars(parts[2]),
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.True(valid);

        using var header = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(parts[0]));
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal(options.KeyId, header.RootElement.GetProperty("kid").GetString());

        using var claims = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(parts[1]));
        Assert.Equal("appstoreconnect-v1", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal(options.IssuerId, claims.RootElement.GetProperty("iss").GetString());
        Assert.Equal(options.BundleId, claims.RootElement.GetProperty("bid").GetString());
        var iat = claims.RootElement.GetProperty("iat").GetInt64();
        var exp = claims.RootElement.GetProperty("exp").GetInt64();
        Assert.InRange(exp - iat, 1, 3600);
    }
}
