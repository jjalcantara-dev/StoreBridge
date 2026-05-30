using Google.Apis.Auth;
using StoreBridge.Android;
using StoreBridge.Android.Internal;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace StoreBridge.Android.Tests;

public sealed class AndroidWebhookAuthenticatorTests
{
    private const string WebhookUrl = "https://api.example.com/webhooks/google";
    private const string ServiceAccountEmail = "pubsub@system.gserviceaccount.com";

    private static AndroidWebhookAuthenticatorOptions DefaultOptions => new()
    {
        WebhookUrl = WebhookUrl
    };

    private static AndroidWebhookAuthenticatorOptions OptionsWithEmail => new()
    {
        WebhookUrl = WebhookUrl,
        ExpectedServiceAccountEmail = ServiceAccountEmail
    };

    [Fact]
    public void Store_IsAndroid()
    {
        var authenticator = new AndroidWebhookAuthenticator(DefaultOptions);
        Assert.Equal(Store.Android, authenticator.Store);
    }

    [Fact]
    public void Constructor_EmptyWebhookUrl_ThrowsArgumentException()
    {
        var options = new AndroidWebhookAuthenticatorOptions { WebhookUrl = string.Empty };
        Assert.Throws<ArgumentException>(() => new AndroidWebhookAuthenticator(options));
    }

    [Fact]
    public void Constructor_WhitespaceWebhookUrl_ThrowsArgumentException()
    {
        var options = new AndroidWebhookAuthenticatorOptions { WebhookUrl = "   " };
        Assert.Throws<ArgumentException>(() => new AndroidWebhookAuthenticator(options));
    }

    [Fact]
    public async Task ValidateAsync_NullBearerToken_ThrowsWebhookAuthenticationException()
    {
        var validator = Substitute.For<IGoogleTokenValidator>();
        var authenticator = new AndroidWebhookAuthenticator(DefaultOptions, validator);

        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync("body", bearerToken: null));
    }

    [Fact]
    public async Task ValidateAsync_EmptyBearerToken_ThrowsWebhookAuthenticationException()
    {
        var validator = Substitute.For<IGoogleTokenValidator>();
        var authenticator = new AndroidWebhookAuthenticator(DefaultOptions, validator);

        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync("body", bearerToken: string.Empty));
    }

    [Fact]
    public async Task ValidateAsync_InvalidToken_ThrowsWebhookAuthenticationException()
    {
        var validator = Substitute.For<IGoogleTokenValidator>();
        validator
            .ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidJwtException("bad token"));

        var authenticator = new AndroidWebhookAuthenticator(DefaultOptions, validator);

        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync("body", bearerToken: "eyJbadtoken"));
    }

    [Fact]
    public async Task ValidateAsync_ValidToken_DoesNotThrow()
    {
        var validator = Substitute.For<IGoogleTokenValidator>();
        validator
            .ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GoogleJsonWebSignature.Payload { Email = ServiceAccountEmail });

        var authenticator = new AndroidWebhookAuthenticator(DefaultOptions, validator);

        await authenticator.ValidateAsync("body", bearerToken: "eyJvalid");
    }

    [Fact]
    public async Task ValidateAsync_StripsBearerPrefix()
    {
        var validator = Substitute.For<IGoogleTokenValidator>();
        string? capturedToken = null;
        validator
            .ValidateAsync(Arg.Do<string>(t => capturedToken = t), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GoogleJsonWebSignature.Payload { Email = ServiceAccountEmail });

        var authenticator = new AndroidWebhookAuthenticator(DefaultOptions, validator);

        await authenticator.ValidateAsync("body", bearerToken: "Bearer eyJrawtoken");

        Assert.Equal("eyJrawtoken", capturedToken);
    }

    [Fact]
    public async Task ValidateAsync_PassesWebhookUrlAsAudience()
    {
        var validator = Substitute.For<IGoogleTokenValidator>();
        string? capturedAudience = null;
        validator
            .ValidateAsync(Arg.Any<string>(), Arg.Do<string>(a => capturedAudience = a), Arg.Any<CancellationToken>())
            .Returns(new GoogleJsonWebSignature.Payload { Email = ServiceAccountEmail });

        var authenticator = new AndroidWebhookAuthenticator(DefaultOptions, validator);

        await authenticator.ValidateAsync("body", bearerToken: "eyJtoken");

        Assert.Equal(WebhookUrl, capturedAudience);
    }

    [Fact]
    public async Task ValidateAsync_EmailMismatch_ThrowsWebhookAuthenticationException()
    {
        var validator = Substitute.For<IGoogleTokenValidator>();
        validator
            .ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GoogleJsonWebSignature.Payload { Email = "wrong@example.com" });

        var authenticator = new AndroidWebhookAuthenticator(OptionsWithEmail, validator);

        await Assert.ThrowsAsync<WebhookAuthenticationException>(
            () => authenticator.ValidateAsync("body", bearerToken: "eyJtoken"));
    }

    [Fact]
    public async Task ValidateAsync_EmailMatch_DoesNotThrow()
    {
        var validator = Substitute.For<IGoogleTokenValidator>();
        validator
            .ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GoogleJsonWebSignature.Payload { Email = ServiceAccountEmail });

        var authenticator = new AndroidWebhookAuthenticator(OptionsWithEmail, validator);

        await authenticator.ValidateAsync("body", bearerToken: "eyJtoken");
    }

    [Fact]
    public async Task ValidateAsync_EmailCheckIsCaseInsensitive()
    {
        var validator = Substitute.For<IGoogleTokenValidator>();
        validator
            .ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GoogleJsonWebSignature.Payload { Email = ServiceAccountEmail.ToUpperInvariant() });

        var authenticator = new AndroidWebhookAuthenticator(OptionsWithEmail, validator);

        // Upper-cased email should still pass the case-insensitive check
        await authenticator.ValidateAsync("body", bearerToken: "eyJtoken");
    }

    [Fact]
    public async Task ValidateAsync_NoEmailOption_AnyEmailAccepted()
    {
        var validator = Substitute.For<IGoogleTokenValidator>();
        validator
            .ValidateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GoogleJsonWebSignature.Payload { Email = "any@example.com" });

        var authenticator = new AndroidWebhookAuthenticator(DefaultOptions, validator);

        // No ExpectedServiceAccountEmail set — any email is fine
        await authenticator.ValidateAsync("body", bearerToken: "eyJtoken");
    }
}
