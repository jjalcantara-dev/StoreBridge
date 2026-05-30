using StoreBridge.Apple;
using StoreBridge.Apple.Internal;
using System.Text.Json;

namespace StoreBridge.Apple.Tests;

public sealed class AppleWebhookParserTests
{
    private readonly AppleWebhookParser _parser = new();

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
    [InlineData("DID_RENEW", null, NotificationEventType.Renewed)]
    [InlineData("SUBSCRIBED", null, NotificationEventType.Created)]
    [InlineData("DID_CHANGE_RENEWAL_STATUS", "AUTO_RENEW_DISABLED", NotificationEventType.AutoRenewDisabled)]
    [InlineData("DID_CHANGE_RENEWAL_STATUS", "AUTO_RENEW_ENABLED", NotificationEventType.AutoRenewEnabled)]
    [InlineData("EXPIRED", null, NotificationEventType.Expired)]
    [InlineData("REFUND", null, NotificationEventType.Refunded)]
    [InlineData("REVOKE", null, NotificationEventType.Refunded)]
    [InlineData("DID_FAIL_TO_RENEW", "GRACE_PERIOD", NotificationEventType.GracePeriod)]
    [InlineData("DID_FAIL_TO_RENEW", null, NotificationEventType.Expired)]
    [InlineData("GRACE_PERIOD_EXPIRED", null, NotificationEventType.InBillingRetry)]
    [InlineData("OFFER_REDEEMED", null, NotificationEventType.Renewed)]
    [InlineData("UNKNOWN_TYPE", null, NotificationEventType.Other)]
    public async Task ParseAsync_MapsNotificationTypeCorrectly(
        string notificationType, string? subtype, NotificationEventType expected)
    {
        var signedPayload = BuildFakeJwt(notificationType, subtype);
        var body = $$"""{"signedPayload": "{{signedPayload}}"}""";

        var result = await _parser.ParseAsync(body);

        Assert.Equal(expected, result.EventType);
    }

    [Theory]
    [InlineData(1, SubscriptionStatus.Active)]
    [InlineData(2, SubscriptionStatus.Expired)]
    [InlineData(3, SubscriptionStatus.InBillingRetry)]
    [InlineData(4, SubscriptionStatus.InGracePeriod)]
    [InlineData(5, SubscriptionStatus.Revoked)]
    [InlineData(99, SubscriptionStatus.Unknown)]
    public async Task ParseAsync_MapsStatusCorrectly(int appleStatus, SubscriptionStatus expected)
    {
        var signedPayload = BuildFakeJwt("DID_RENEW", null, status: appleStatus);
        var body = $$"""{"signedPayload": "{{signedPayload}}"}""";

        var result = await _parser.ParseAsync(body);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task ParseAsync_AcceptsRawJwtWithoutJsonWrapper()
    {
        var signedPayload = BuildFakeJwt("DID_RENEW", null);
        var result = await _parser.ParseAsync(signedPayload);

        Assert.Equal(NotificationEventType.Renewed, result.EventType);
        Assert.Equal(Store.Apple, result.Store);
    }

    [Fact]
    public async Task ParseAsync_TestNotificationType_MapsToTest()
    {
        var signedPayload = BuildFakeJwt("TEST", null);
        var body = $$"""{"signedPayload": "{{signedPayload}}"}""";

        var result = await _parser.ParseAsync(body);

        Assert.Equal(NotificationEventType.Test, result.EventType);
    }

    [Fact]
    public async Task ParseAsync_NotificationUUID_IsSurfacedAsNotificationId()
    {
        var uuid = Guid.NewGuid().ToString();
        var signedPayload = BuildFakeJwt("DID_RENEW", null, notificationUUID: uuid);
        var body = $$"""{"signedPayload": "{{signedPayload}}"}""";

        var result = await _parser.ParseAsync(body);

        Assert.Equal(uuid, result.NotificationId);
    }

    [Fact]
    public async Task ParseAsync_SandboxEnvironment_SetsSandboxFlag()
    {
        var signedPayload = BuildFakeJwt("DID_RENEW", null, environment: "Sandbox");
        var body = $$"""{"signedPayload": "{{signedPayload}}"}""";

        var result = await _parser.ParseAsync(body);

        Assert.True(result.IsSandbox);
    }

    // Builds a fake (unsigned) JWT for testing the parser's payload extraction logic.
    // The parser does not validate JWT signatures, only decodes the payload.
    private static string BuildFakeJwt(
        string notificationType,
        string? subtype,
        int status = 1,
        string environment = "Production",
        string? notificationUUID = null)
    {
        var header = Base64UrlEncode("""{"alg":"ES256","kid":"TEST"}""");

        var data = new
        {
            status,
            environment,
            signedTransactionInfo = BuildFakeTransactionJwt()
        };

        var payloadObj = new
        {
            notificationType,
            subtype,
            data,
            signedDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            notificationUUID
        };

        var payload = Base64UrlEncode(JsonSerializer.Serialize(payloadObj));
        return $"{header}.{payload}.fakesignature";
    }

    private static string BuildFakeTransactionJwt()
    {
        var header = Base64UrlEncode("""{"alg":"ES256"}""");
        var txPayload = new
        {
            originalTransactionId = "1000000000000001",
            productId = "premium_monthly",
            expiresDate = DateTimeOffset.UtcNow.AddMonths(1).ToUnixTimeMilliseconds(),
            purchaseDate = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds(),
            environment = "Production"
        };
        var payload = Base64UrlEncode(JsonSerializer.Serialize(txPayload));
        return $"{header}.{payload}.fakesig";
    }

    private static string Base64UrlEncode(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
