using StoreBridge.Apple.Tests.Fixtures;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace StoreBridge.Apple.Tests;

/// <summary>
/// End-to-end checks against a notification genuinely signed by Apple: the built-in Apple Root CA - G3,
/// Apple's real WWDR intermediate and App Store leaf (with their marker OIDs) and Apple's ES256 signature.
/// Certificate validity is evaluated at the notification's signing time because the leaf has since expired.
/// </summary>
public sealed class AppleRealNotificationTests
{
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly TimeProvider AtSigningTime = new FixedTimeProvider(RealAppleNotifications.SignedAt.AddMinutes(1));

    private static string Body(string jws) => $$"""{"signedPayload":"{{jws}}"}""";

    private static AppleWebhookAuthenticator Authenticator(TimeProvider time, Action<AppleWebhookAuthenticatorOptions>? configure = null)
    {
        var options = new AppleWebhookAuthenticatorOptions { BundleId = RealAppleNotifications.BundleId };
        configure?.Invoke(options);
        return new AppleWebhookAuthenticator(options, time);
    }

    [Fact]
    public async Task RealAppleNotification_AtSigningTime_IsAccepted()
    {
        using var authenticator = Authenticator(AtSigningTime);

        await authenticator.ValidateAsync(Body(RealAppleNotifications.SandboxTestNotification));
    }

    [Fact]
    public async Task RealAppleNotification_Today_IsRejectedBecauseLeafExpired()
    {
        using var authenticator = Authenticator(TimeProvider.System);

        var ex = await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Body(RealAppleNotifications.SandboxTestNotification)));
        Assert.Contains("chain validation failed", ex.Message);
    }

    [Fact]
    public async Task RealAppleNotification_TodayWithValidateCertificateAtSignedDate_IsAccepted()
    {
        using var authenticator = Authenticator(TimeProvider.System, o => o.ValidateCertificateAtSignedDate = true);

        await authenticator.ValidateAsync(Body(RealAppleNotifications.SandboxTestNotification));
    }

    [Fact]
    public async Task RealAppleNotification_ForAnotherBundle_IsRejected()
    {
        using var authenticator = Authenticator(AtSigningTime, o => o.BundleId = "com.example.app");

        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Body(RealAppleNotifications.SandboxTestNotification)));
    }

    [Fact]
    public async Task RealAppleNotification_SandboxDisallowed_IsRejected()
    {
        using var authenticator = Authenticator(AtSigningTime, o => o.AllowSandbox = false);

        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync(Body(RealAppleNotifications.SandboxTestNotification)));
    }

    [Fact]
    public async Task RealAppleNotification_WithRewrittenPayload_IsRejected()
    {
        // Keep Apple's genuine header and signature, but claim the notification is for another app
        var parts = RealAppleNotifications.SandboxTestNotification.Split('.');
        var payload = JsonNode.Parse(Convert.FromBase64String(Pad(parts[1].Replace('-', '+').Replace('_', '/'))))!;
        payload["data"]!["bundleId"] = "com.example.app";
        var forged = $"{parts[0]}.{B64(JsonSerializer.SerializeToUtf8Bytes(payload))}.{parts[2]}";

        using var authenticator = Authenticator(AtSigningTime, o => o.BundleId = "com.example.app");

        var ex = await Assert.ThrowsAsync<WebhookAuthenticationException>(() => authenticator.ValidateAsync(Body(forged)));
        Assert.Contains("signature is invalid", ex.Message);
    }

    [Fact]
    public async Task RealAppleNotification_IsParsedAsSandboxTest()
    {
        var notification = await new AppleWebhookParser().ParseAsync(Body(RealAppleNotifications.SandboxTestNotification));

        Assert.Equal(NotificationEventType.Test, notification.EventType);
        Assert.True(notification.IsSandbox);
        Assert.Equal("69acc1b4-a36b-4733-956a-847834962586", notification.NotificationId);
        Assert.Equal(RealAppleNotifications.SignedAt, notification.EventAt);
    }

    private static string Pad(string s) => s + new string('=', (4 - s.Length % 4) % 4);

    private static string B64(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
