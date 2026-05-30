using StoreBridge.Android;
using System.Text;
using System.Text.Json;

namespace StoreBridge.Android.Tests;

public sealed class AndroidInAppPurchaseWebhookParserTests
{
    private readonly AndroidInAppPurchaseWebhookParser _parser = new();

    [Fact]
    public void Store_IsAndroid() => Assert.Equal(Store.Android, _parser.Store);

    [Fact]
    public async Task ParseAsync_EmptyBody_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _parser.ParseAsync(string.Empty));
    }

    [Fact]
    public async Task ParseAsync_MissingMessageData_Throws()
    {
        var body = """{"message": {}, "subscription": "test"}""";
        await Assert.ThrowsAsync<FormatException>(() => _parser.ParseAsync(body));
    }

    [Fact]
    public async Task ParseAsync_SubscriptionNotification_Throws()
    {
        var notification = new
        {
            version = "1.0",
            packageName = "com.example",
            eventTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            subscriptionNotification = new { version = "1.0", notificationType = 2, purchaseToken = "tok", subscriptionId = "sub" }
        };
        var body = BuildPubSubBody(notification);
        await Assert.ThrowsAsync<FormatException>(() => _parser.ParseAsync(body));
    }

    [Theory]
    [InlineData(1, InAppPurchaseEventType.Purchased)]
    [InlineData(2, InAppPurchaseEventType.Cancelled)]
    [InlineData(99, InAppPurchaseEventType.Other)]
    public async Task ParseAsync_MapsNotificationTypeCorrectly(int notificationType, InAppPurchaseEventType expected)
    {
        var body = BuildOneTimeProductBody("com.example", notificationType, "token", "coins_100");
        var result = await _parser.ParseAsync(body);
        Assert.Equal(expected, result.EventType);
    }

    [Fact]
    public async Task ParseAsync_ExtractsPurchaseTokenAndSku()
    {
        var body = BuildOneTimeProductBody("com.example", 1, "my-token", "coins_100");
        var result = await _parser.ParseAsync(body);
        Assert.Equal("my-token", result.PurchaseId);
        Assert.Equal("coins_100", result.ProductId);
    }

    [Fact]
    public async Task ParseAsync_SetsCorrectStore()
    {
        var body = BuildOneTimeProductBody("com.example", 1, "token", "product");
        var result = await _parser.ParseAsync(body);
        Assert.Equal(Store.Android, result.Store);
    }

    [Fact]
    public async Task ParseAsync_VoidedPurchaseNotification_MapsToRefunded()
    {
        var notification = new
        {
            version = "1.0",
            packageName = "com.example",
            eventTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            voidedPurchaseNotification = new
            {
                purchaseToken = "voided-token",
                orderId = "GPA.0000-0000-0000",
                refundType = 1,
                productType = 1
            }
        };
        var result = await _parser.ParseAsync(BuildPubSubBody(notification));

        Assert.Equal(InAppPurchaseEventType.Refunded, result.EventType);
        Assert.Equal("voided-token", result.PurchaseId);
        Assert.Equal("GPA.0000-0000-0000", result.ProductId);
        Assert.StartsWith("VOIDED:", result.RawEventType);
    }

    [Fact]
    public async Task ParseAsync_TestNotification_MapsToTest()
    {
        var notification = new
        {
            version = "1.0",
            packageName = "com.example",
            eventTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            testNotification = new { version = "1.0" }
        };
        var result = await _parser.ParseAsync(BuildPubSubBody(notification));

        Assert.Equal(InAppPurchaseEventType.Test, result.EventType);
    }

    [Fact]
    public async Task ParseAsync_PubSubMessageId_SurfacedAsNotificationId()
    {
        var inner = new
        {
            version = "1.0",
            packageName = "com.example",
            eventTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            oneTimeProductNotification = new { version = "1.0", notificationType = 1, purchaseToken = "tk", sku = "p" }
        };
        var json = JsonSerializer.Serialize(inner);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var envelope = new
        {
            message = new { data = encoded, messageId = "msg-iap-7", publishTime = DateTime.UtcNow.ToString("o") },
            subscription = "projects/test/subscriptions/test"
        };

        var result = await _parser.ParseAsync(JsonSerializer.Serialize(envelope));

        Assert.Equal("msg-iap-7", result.NotificationId);
    }

    [Fact]
    public async Task ParseAsync_SetsEventAt()
    {
        var eventTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var notification = new
        {
            version = "1.0",
            packageName = "com.example",
            eventTimeMillis = eventTime,
            oneTimeProductNotification = new { version = "1.0", notificationType = 1, purchaseToken = "tok", sku = "prod" }
        };
        var result = await _parser.ParseAsync(BuildPubSubBody(notification));
        Assert.NotNull(result.EventAt);
    }

    private static string BuildOneTimeProductBody(
        string packageName, int notificationType, string purchaseToken, string sku)
    {
        var notification = new
        {
            version = "1.0",
            packageName,
            eventTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            oneTimeProductNotification = new { version = "1.0", notificationType, purchaseToken, sku }
        };
        return BuildPubSubBody(notification);
    }

    private static string BuildPubSubBody(object notification)
    {
        var json = JsonSerializer.Serialize(notification);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var message = new
        {
            message = new { data = encoded, messageId = "1", publishTime = DateTime.UtcNow.ToString("o") },
            subscription = "projects/test/subscriptions/test"
        };
        return JsonSerializer.Serialize(message);
    }
}
