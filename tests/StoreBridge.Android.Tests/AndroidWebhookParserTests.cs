using StoreBridge.Android;
using System.Text;
using System.Text.Json;

namespace StoreBridge.Android.Tests;

public sealed class AndroidWebhookParserTests
{
    private readonly AndroidWebhookParser _parser = new();

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
    public async Task ParseAsync_MissingSubscriptionNotification_Throws()
    {
        var notification = new { version = "1.0", packageName = "com.example" };
        var body = BuildPubSubBody(notification);
        await Assert.ThrowsAsync<FormatException>(() => _parser.ParseAsync(body));
    }

    [Theory]
    [InlineData(1, NotificationEventType.Renewed)]
    [InlineData(2, NotificationEventType.Renewed)]
    [InlineData(3, NotificationEventType.AutoRenewDisabled)]
    [InlineData(4, NotificationEventType.Created)]
    [InlineData(5, NotificationEventType.InBillingRetry)]
    [InlineData(6, NotificationEventType.GracePeriod)]
    [InlineData(7, NotificationEventType.Renewed)]
    [InlineData(12, NotificationEventType.Refunded)]
    [InlineData(13, NotificationEventType.Expired)]
    [InlineData(99, NotificationEventType.Other)]
    public async Task ParseAsync_MapsNotificationTypeCorrectly(int notificationType, NotificationEventType expected)
    {
        var body = BuildSubscriptionNotificationBody("com.example.app", notificationType, "token123", "premium_monthly");

        var result = await _parser.ParseAsync(body);

        Assert.Equal(expected, result.EventType);
    }

    [Theory]
    [InlineData(1, SubscriptionStatus.Active)]
    [InlineData(2, SubscriptionStatus.Active)]
    [InlineData(3, SubscriptionStatus.Cancelled)]
    [InlineData(4, SubscriptionStatus.Active)]
    [InlineData(5, SubscriptionStatus.InBillingRetry)]
    [InlineData(6, SubscriptionStatus.InGracePeriod)]
    [InlineData(12, SubscriptionStatus.Revoked)]
    [InlineData(13, SubscriptionStatus.Expired)]
    public async Task ParseAsync_MapsStatusCorrectly(int notificationType, SubscriptionStatus expected)
    {
        var body = BuildSubscriptionNotificationBody("com.example.app", notificationType, "token123", "sub_id");

        var result = await _parser.ParseAsync(body);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task ParseAsync_ExtractsPurchaseTokenAndSubscriptionId()
    {
        var body = BuildSubscriptionNotificationBody("com.example", 2, "my-purchase-token", "premium_monthly");

        var result = await _parser.ParseAsync(body);

        Assert.Equal("my-purchase-token", result.SubscriptionId);
        Assert.Equal("premium_monthly", result.ProductId);
    }

    [Fact]
    public async Task ParseAsync_TestNotification_MapsToTest()
    {
        var notification = new
        {
            version = "1.0",
            packageName = "com.example.app",
            eventTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            testNotification = new { version = "1.0" }
        };
        var body = BuildPubSubBody(notification);

        var result = await _parser.ParseAsync(body);

        Assert.Equal(NotificationEventType.Test, result.EventType);
        Assert.Equal("TEST", result.RawEventType);
    }

    [Fact]
    public async Task ParseAsync_PubSubMessageId_SurfacedAsNotificationId()
    {
        var inner = new
        {
            version = "1.0",
            packageName = "com.example.app",
            eventTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            subscriptionNotification = new { version = "1.0", notificationType = 2, purchaseToken = "tk", subscriptionId = "sid" }
        };
        var json = JsonSerializer.Serialize(inner);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var envelope = new
        {
            message = new { data = encoded, messageId = "msg-12345", publishTime = DateTime.UtcNow.ToString("o") },
            subscription = "projects/test/subscriptions/test"
        };

        var result = await _parser.ParseAsync(JsonSerializer.Serialize(envelope));

        Assert.Equal("msg-12345", result.NotificationId);
    }

    [Fact]
    public async Task ParseAsync_SetsCorrectStore()
    {
        var body = BuildSubscriptionNotificationBody("com.example", 2, "token", "sub");

        var result = await _parser.ParseAsync(body);

        Assert.Equal(Store.Android, result.Store);
    }

    private static string BuildSubscriptionNotificationBody(
        string packageName, int notificationType, string purchaseToken, string subscriptionId)
    {
        var notification = new
        {
            version = "1.0",
            packageName,
            eventTimeMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            subscriptionNotification = new
            {
                version = "1.0",
                notificationType,
                purchaseToken,
                subscriptionId
            }
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
