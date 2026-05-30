using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StoreBridge.Apple;
using System.Security.Cryptography;

namespace StoreBridge.Apple.Tests;

public sealed class AppleServiceCollectionExtensionsTests
{
    private static string CreateEc256Key()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Convert.ToBase64String(key.ExportPkcs8PrivateKey());
    }

    private static Action<AppleSubscriptionOptions> ValidSubscriptionOptions() => opts =>
    {
        opts.KeyId            = "K";
        opts.IssuerId         = "I";
        opts.BundleId         = "com.example.app";
        opts.PrivateKeyBase64 = CreateEc256Key();
    };

    [Fact]
    public void AddAppleSubscriptions_RegistersVerifierAndInterface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppleSubscriptions(opts =>
        {
            opts.KeyId = "K";
            opts.IssuerId = "I";
            opts.BundleId = "com.example.app";
            opts.PrivateKeyBase64 = CreateEc256Key();
        });

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<AppleSubscriptionVerifier>());
        Assert.NotNull(sp.GetService<ISubscriptionVerifier>());
        Assert.Same(sp.GetService<AppleSubscriptionVerifier>(), sp.GetService<ISubscriptionVerifier>());
    }

    [Fact]
    public void AddAppleInAppPurchases_RegistersVerifierAndInterface()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppleInAppPurchases(opts =>
        {
            opts.KeyId = "K";
            opts.IssuerId = "I";
            opts.BundleId = "com.example.app";
            opts.PrivateKeyBase64 = CreateEc256Key();
        });

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<AppleInAppPurchaseVerifier>());
        Assert.NotNull(sp.GetService<IInAppPurchaseVerifier>());
    }

    [Fact]
    public void AddAppleWebhooks_RegistersAuthenticatorAndParsers()
    {
        var services = new ServiceCollection();
        services.AddAppleWebhooks();

        using var sp = services.BuildServiceProvider();

        Assert.NotNull(sp.GetService<IWebhookAuthenticator>());
        Assert.NotNull(sp.GetService<IWebhookParser>());
        Assert.NotNull(sp.GetService<IInAppPurchaseWebhookParser>());
    }

    [Fact]
    public void AddAppleSubscriptions_MissingKeyId_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppleSubscriptions(opts =>
        {
            opts.IssuerId         = "I";
            opts.BundleId         = "com.example.app";
            opts.PrivateKeyBase64 = CreateEc256Key();
        });

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<AppleSubscriptionVerifier>());
        Assert.Contains("KeyId", ex.Message);
    }

    [Fact]
    public void AddAppleSubscriptions_BadBase64Key_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppleSubscriptions(opts =>
        {
            opts.KeyId            = "K";
            opts.IssuerId         = "I";
            opts.BundleId         = "com.example.app";
            opts.PrivateKeyBase64 = "@@@not-base64@@@";
        });

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<AppleSubscriptionVerifier>());
        Assert.Contains("base64", ex.Message);
    }

    [Fact]
    public void AddAppleSubscriptions_NonPkcs8Key_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppleSubscriptions(opts =>
        {
            opts.KeyId            = "K";
            opts.IssuerId         = "I";
            opts.BundleId         = "com.example.app";
            // Valid base64 but not a PKCS#8 EC private key
            opts.PrivateKeyBase64 = Convert.ToBase64String(new byte[] { 0x01, 0x02, 0x03 });
        });

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<AppleSubscriptionVerifier>());
        Assert.Contains("PKCS#8", ex.Message);
    }

    [Fact]
    public void AddAppleSubscriptions_NonHttpEndpoint_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppleSubscriptions(opts =>
        {
            opts.KeyId                = "K";
            opts.IssuerId             = "I";
            opts.BundleId             = "com.example.app";
            opts.PrivateKeyBase64     = CreateEc256Key();
            opts.SubscriptionsBaseUrl = "not-a-url";
        });

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<AppleSubscriptionVerifier>());
        Assert.Contains("http", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddAppleSubscriptions_MaxRetriesBelowOne_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAppleSubscriptions(opts =>
        {
            opts.KeyId            = "K";
            opts.IssuerId         = "I";
            opts.BundleId         = "com.example.app";
            opts.PrivateKeyBase64 = CreateEc256Key();
            opts.MaxRetries       = 0;
        });

        using var sp = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => sp.GetRequiredService<AppleSubscriptionVerifier>());
        Assert.Contains("MaxRetries", ex.Message);
    }
}
