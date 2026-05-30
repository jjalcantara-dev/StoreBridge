using StoreBridge.Apple;
using System.Text.Json;

namespace StoreBridge.Apple.Tests;

public sealed class AppleInAppPurchaseWebhookParserTests
{
    private readonly AppleInAppPurchaseWebhookParser _parser = new();

    [Fact]
    public void Store_IsApple() => Assert.Equal(Store.Apple, _parser.Store);

    [Fact]
    public async Task ParseAsync_EmptyBody_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _parser.ParseAsync(string.Empty));
    }

    [Fact]
    public async Task ParseAsync_MissingSignedPayload_Throws()
    {
        await Assert.ThrowsAsync<FormatException>(() => _parser.ParseAsync("{}"));
    }

    [Fact]
    public async Task ParseAsync_InvalidJwt_Throws()
    {
        var body = """{"signedPayload": "notavalidjwt"}""";
        await Assert.ThrowsAsync<FormatException>(() => _parser.ParseAsync(body));
    }

    [Theory]
    [InlineData("REFUND", InAppPurchaseEventType.Refunded)]
    [InlineData("REVOKE", InAppPurchaseEventType.Refunded)]
    [InlineData("CONSUMPTION_REQUEST", InAppPurchaseEventType.ConsumptionRequest)]
    [InlineData("DID_RENEW", InAppPurchaseEventType.Other)]
    [InlineData("UNKNOWN_TYPE", InAppPurchaseEventType.Other)]
    public async Task ParseAsync_MapsNotificationTypeCorrectly(
        string notificationType, InAppPurchaseEventType expected)
    {
        var body = BuildBody(notificationType);
        var result = await _parser.ParseAsync(body);
        Assert.Equal(expected, result.EventType);
    }

    [Fact]
    public async Task ParseAsync_AcceptsRawJwtWithoutJsonWrapper()
    {
        var signedPayload = BuildFakeJwt("REFUND");
        var result = await _parser.ParseAsync(signedPayload);
        Assert.Equal(InAppPurchaseEventType.Refunded, result.EventType);
    }

    [Fact]
    public async Task ParseAsync_SandboxEnvironment_SetsSandboxFlag()
    {
        var body = BuildBody("REFUND", environment: "Sandbox");
        var result = await _parser.ParseAsync(body);
        Assert.True(result.IsSandbox);
    }

    [Fact]
    public async Task ParseAsync_ProductionEnvironment_DoesNotSetSandboxFlag()
    {
        var body = BuildBody("REFUND", environment: "Production");
        var result = await _parser.ParseAsync(body);
        Assert.False(result.IsSandbox);
    }

    [Fact]
    public async Task ParseAsync_ExtractsPurchaseIdAndProductId()
    {
        var body = BuildBody("REFUND", transactionId: "tx-abc-123", productId: "coins_100");
        var result = await _parser.ParseAsync(body);
        Assert.Equal("tx-abc-123", result.PurchaseId);
        Assert.Equal("coins_100", result.ProductId);
    }

    private static string BuildBody(
        string notificationType,
        string environment = "Production",
        string transactionId = "tx-001",
        string productId = "product_id")
    {
        var signedPayload = BuildFakeJwt(notificationType, environment, transactionId, productId);
        return $$"""{"signedPayload": "{{signedPayload}}"}""";
    }

    private static string BuildFakeJwt(
        string notificationType,
        string environment = "Production",
        string transactionId = "tx-001",
        string productId = "product_id")
    {
        var header = Base64UrlEncode("""{"alg":"ES256","kid":"TEST"}""");

        var txPayload = new { transactionId, productId, environment };
        var txJwt = $"{Base64UrlEncode("""{"alg":"ES256"}""")}.{Base64UrlEncode(JsonSerializer.Serialize(txPayload))}.fakesig";

        var data = new { environment, signedTransactionInfo = txJwt };
        var payloadObj = new
        {
            notificationType,
            data,
            signedDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        return $"{header}.{Base64UrlEncode(JsonSerializer.Serialize(payloadObj))}.fakesignature";
    }

    private static string Base64UrlEncode(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
