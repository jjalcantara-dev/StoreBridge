using StoreBridge.Apple;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace StoreBridge.Apple.Tests;

public sealed class AppleSubscriptionVerifierTests
{
    // ── helpers ────────────────────────────────────────────────

    private static AppleSubscriptionOptions BuildOptions(int maxRetries = 1) => new()
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

    /// <summary>Builds a full Apple subscriptions-API JSON response with one group and one transaction.</summary>
    private static string BuildResponse(
        string productId = "premium_monthly",
        int status = 1,
        long? expiresDate = null,
        long? purchaseDate = null,
        int? autoRenewStatus = null,
        string environment = "Production",
        string? autoRenewProductId = null,
        long? gracePeriodExpiresDate = null)
    {
        expiresDate ??= DateTimeOffset.UtcNow.AddMonths(1).ToUnixTimeMilliseconds();
        purchaseDate ??= DateTimeOffset.UtcNow.AddMonths(-1).ToUnixTimeMilliseconds();

        var signedTx = Jwt(new
        {
            originalTransactionId = "tx-001",
            transactionId = "tx-001",
            productId,
            expiresDate,
            purchaseDate,
            price = 9990L,
            currency = "USD",
            environment
        });

        var hasRenewalInfo = autoRenewStatus.HasValue
            || autoRenewProductId != null
            || gracePeriodExpiresDate.HasValue;
        string? signedRenewal = hasRenewalInfo
            ? Jwt(new
            {
                autoRenewStatus,
                autoRenewProductId,
                gracePeriodExpiresDate
            })
            : null;

        return JsonSerializer.Serialize(new
        {
            environment,
            data = new[]
            {
                new
                {
                    subscriptionGroupIdentifier = "group-001",
                    lastTransactions = new[]
                    {
                        new { originalTransactionId = "tx-001", productId, status, signedTransactionInfo = signedTx, signedRenewalInfo = signedRenewal }
                    }
                }
            }
        });
    }

    // ── basic verification ──────────────────────────────────────

    [Fact]
    public async Task VerifySubscriptionAsync_ValidActiveResponse_ReturnsVerified()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, BuildResponse(status: 1)));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.True(result.IsVerified);
        Assert.Equal(Store.Apple, result.Store);
        Assert.Equal(SubscriptionStatus.Active, result.Status);
        Assert.Equal("premium_monthly", result.ProductId);
        Assert.NotNull(result.ExpiresAt);
        Assert.NotNull(result.PurchasedAt);
    }

    [Theory]
    [InlineData(1, SubscriptionStatus.Active)]
    [InlineData(2, SubscriptionStatus.Expired)]
    [InlineData(3, SubscriptionStatus.InBillingRetry)]
    [InlineData(4, SubscriptionStatus.InGracePeriod)]
    [InlineData(5, SubscriptionStatus.Revoked)]
    public async Task VerifySubscriptionAsync_MapsAppleStatusCorrectly(int appleStatus, SubscriptionStatus expected)
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, BuildResponse(status: appleStatus)));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.Equal(expected, result.Status);
    }

    // ── Bug #2: CancelledByUser via signedRenewalInfo ──────────

    [Fact]
    public async Task VerifySubscriptionAsync_AutoRenewDisabled_SetsCancelledByUser()
    {
        // autoRenewStatus=0 means the user turned off auto-renew
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, BuildResponse(autoRenewStatus: 0)));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.True(result.CancelledByUser);
    }

    [Fact]
    public async Task VerifySubscriptionAsync_AutoRenewEnabled_DoesNotSetCancelledByUser()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, BuildResponse(autoRenewStatus: 1)));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.False(result.CancelledByUser);
    }

    [Fact]
    public async Task VerifySubscriptionAsync_NoRenewalInfo_CancelledByUserIsFalse()
    {
        // No signedRenewalInfo in response → renewalInfo is null → CancelledByUser = false
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, BuildResponse(autoRenewStatus: null)));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.False(result.CancelledByUser);
    }

    // ── renewal info: extra fields ──────────────────────────────

    [Fact]
    public async Task VerifySubscriptionAsync_RenewalInfo_SurfacesAutoRenewProductId()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK,
            BuildResponse(autoRenewStatus: 1, autoRenewProductId: "premium_yearly")));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.Equal("premium_yearly", result.AutoRenewProductId);
    }

    [Fact]
    public async Task VerifySubscriptionAsync_RenewalInfo_SurfacesGracePeriodExpiresAt()
    {
        var grace = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds();
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK,
            BuildResponse(autoRenewStatus: 1, gracePeriodExpiresDate: grace)));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(grace), result.GracePeriodExpiresAt);
    }

    [Fact]
    public async Task VerifySubscriptionAsync_NoRenewalInfo_ExtraFieldsAreDefault()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, BuildResponse()));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.Equal(string.Empty, result.AutoRenewProductId);
        Assert.Null(result.GracePeriodExpiresAt);
    }

    // ── sandbox detection ───────────────────────────────────────

    [Fact]
    public async Task VerifySubscriptionAsync_SandboxEnvironment_SetsSandboxFlag()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, BuildResponse(environment: "Sandbox")));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.True(result.IsSandbox);
    }

    [Fact]
    public async Task VerifySubscriptionAsync_ProductionEnvironment_DoesNotSetSandboxFlag()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, BuildResponse(environment: "Production")));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.False(result.IsSandbox);
    }

    // ── price conversion ────────────────────────────────────────

    [Fact]
    public async Task VerifySubscriptionAsync_PriceIsConvertedFromThousandths()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, BuildResponse()));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.Equal(9990L, result.PriceAmount);
        Assert.Equal(9.990m, result.PriceDecimal);
        Assert.Equal("USD", result.CurrencyCode);
    }

    // ── error handling ──────────────────────────────────────────

    [Fact]
    public async Task VerifySubscriptionAsync_NonSuccessResponse_ReturnsFailed()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.Unauthorized, """{"errorCode":4010008}"""));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.False(result.IsVerified);
        Assert.Contains("401", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifySubscriptionAsync_EmptyData_ReturnsFailed()
    {
        var body = """{"environment":"Production","data":[]}""";
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, body));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token-001", "premium_monthly");

        Assert.False(result.IsVerified);
    }

    // ── retry behavior ──────────────────────────────────────────

    [Fact]
    public async Task VerifySubscriptionAsync_4xxResponse_DoesNotRetry()
    {
        // Even with MaxRetries=3, a 4xx must not be retried
        var handler = new FakeHttpMessageHandler(
            (HttpStatusCode.Unauthorized, "{}"),
            (HttpStatusCode.Unauthorized, "{}"),
            (HttpStatusCode.Unauthorized, "{}"));
        var options = BuildOptions(maxRetries: 3);
        using var verifier = new AppleSubscriptionVerifier(options, new HttpClient(handler));

        await verifier.VerifySubscriptionAsync("token", "premium_monthly");

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task VerifySubscriptionAsync_5xxThenSuccess_Retries()
    {
        var handler = new FakeHttpMessageHandler(
            (HttpStatusCode.InternalServerError, "server error"),
            (HttpStatusCode.OK, BuildResponse()));
        var options = BuildOptions(maxRetries: 2);
        using var verifier = new AppleSubscriptionVerifier(options, new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token", "premium_monthly");

        Assert.Equal(2, handler.CallCount);
        Assert.True(result.IsVerified);
    }

    [Fact]
    public async Task VerifySubscriptionAsync_All5xxExhausted_ReturnsFailed()
    {
        var handler = new FakeHttpMessageHandler(
            (HttpStatusCode.InternalServerError, "error"),
            (HttpStatusCode.InternalServerError, "error"));
        var options = BuildOptions(maxRetries: 2);
        using var verifier = new AppleSubscriptionVerifier(options, new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token", "premium_monthly");

        Assert.Equal(2, handler.CallCount);
        Assert.False(result.IsVerified);
    }

    // ── robust exception handling ───────────────────────────────

    [Fact]
    public async Task VerifySubscriptionAsync_EmptyToken_ReturnsFailureWithoutCallingApi()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, ""));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("", "premium_monthly");

        Assert.False(result.IsVerified);
        Assert.Equal(0, handler.CallCount);
        Assert.Contains("required", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifySubscriptionAsync_MalformedJsonResponse_ReturnsFailure()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, "not valid json {{"));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token", "premium_monthly");

        Assert.False(result.IsVerified);
        Assert.Contains("malformed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifySubscriptionAsync_InvalidPrivateKey_ReturnsFailureWithClearMessage()
    {
        var options = BuildOptions();
        options.PrivateKeyBase64 = Convert.ToBase64String(new byte[] { 0x01, 0x02, 0x03 }); // valid base64, junk PKCS#8
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, ""));
        using var verifier = new AppleSubscriptionVerifier(options, new HttpClient(handler));

        var result = await verifier.VerifySubscriptionAsync("token", "premium_monthly");

        Assert.False(result.IsVerified);
        Assert.Equal(0, handler.CallCount);
        Assert.Contains("PKCS#8", result.ErrorMessage);
    }

    [Fact]
    public async Task VerifySubscriptionAsync_Cancelled_PropagatesCancellation()
    {
        var handler = new FakeHttpMessageHandler((HttpStatusCode.OK, BuildResponse()));
        using var verifier = new AppleSubscriptionVerifier(BuildOptions(), new HttpClient(handler));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifySubscriptionAsync("token", "premium_monthly", cts.Token));
    }
}
