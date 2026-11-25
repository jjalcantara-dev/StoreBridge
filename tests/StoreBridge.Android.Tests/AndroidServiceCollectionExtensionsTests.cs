using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StoreBridge.Android;

namespace StoreBridge.Android.Tests;

public sealed class AndroidServiceCollectionExtensionsTests
{
    [Fact]
    public void AddAndroidSubscriptions_RegistersVerifierAndInterface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAndroidSubscriptions(opts =>
        {
            opts.PackageName       = "com.example.app";
            opts.CredentialsBase64 = TestCredentials.ServiceAccountBase64;
        });

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<AndroidSubscriptionVerifier>());
        Assert.NotNull(sp.GetService<ISubscriptionVerifier>());
        Assert.Same(sp.GetService<AndroidSubscriptionVerifier>(), sp.GetService<ISubscriptionVerifier>());
    }

    [Fact]
    public void AddAndroidWebhooks_RegistersAuthenticatorAndParsers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAndroidWebhooks(opts =>
        {
            opts.WebhookUrl = "https://example.com/webhook";
            opts.ExpectedServiceAccountEmail = "pubsub@example.iam.gserviceaccount.com";
        });

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<AndroidWebhookAuthenticator>());
        Assert.NotNull(sp.GetService<IWebhookAuthenticator>());
        Assert.NotNull(sp.GetService<IWebhookParser>());
        Assert.NotNull(sp.GetService<IInAppPurchaseWebhookParser>());
    }

    [Fact]
    public void AddAndroidSubscriptions_MissingCredentials_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAndroidSubscriptions(opts => opts.PackageName = "com.example.app");

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<AndroidSubscriptionVerifier>());
        Assert.Contains("CredentialsBase64", ex.Message);
    }

    [Fact]
    public void AddAndroidSubscriptions_BadBase64_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAndroidSubscriptions(opts =>
        {
            opts.PackageName       = "com.example.app";
            opts.CredentialsBase64 = "@@@not-base64@@@";
        });

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<AndroidSubscriptionVerifier>());
        Assert.Contains("base64", ex.Message);
    }

    [Fact]
    public void AddAndroidWebhooks_MissingUrl_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAndroidWebhooks(opts => { });

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<AndroidWebhookAuthenticator>());
        Assert.Contains("WebhookUrl", ex.Message);
    }

    [Fact]
    public void AddAndroidWebhooks_RelativeUrl_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAndroidWebhooks(opts => opts.WebhookUrl = "/webhooks/google");

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<AndroidWebhookAuthenticator>());
        Assert.Contains("absolute", ex.Message);
    }

    [Fact]
    public void AddAndroidWebhooks_MissingServiceAccountEmail_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAndroidWebhooks(opts => opts.WebhookUrl = "https://example.com/webhook");

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<AndroidWebhookAuthenticator>());
        Assert.Contains("ExpectedServiceAccountEmail", ex.Message);
    }

    [Fact]
    public void AddAndroidSubscriptions_NonServiceAccountCredentials_ThrowsOnResolve()
    {
        // An authorized_user (or external_account) JSON must be rejected: only service-account keys are allowed
        var json = """{"type":"authorized_user","client_id":"x","client_secret":"y","refresh_token":"z"}""";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAndroidSubscriptions(opts =>
        {
            opts.PackageName = "com.example.app";
            opts.CredentialsBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        });

        using var sp = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<AndroidSubscriptionVerifier>());
    }
}
