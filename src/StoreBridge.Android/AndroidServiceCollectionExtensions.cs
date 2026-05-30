using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StoreBridge.Android.Internal;

namespace StoreBridge.Android;

/// <summary>
/// <see cref="IServiceCollection"/> extensions to register Google Play verifiers, parsers,
/// and the webhook authenticator.
/// </summary>
public static class AndroidServiceCollectionExtensions
{
    /// <summary>Registers <see cref="AndroidSubscriptionVerifier"/> with options binding.</summary>
    public static IServiceCollection AddAndroidSubscriptions(
        this IServiceCollection services,
        Action<AndroidSubscriptionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        return AddSubscriptionVerifierCore(services);
    }

    /// <summary>Binds <see cref="AndroidSubscriptionOptions"/> from an <see cref="IConfiguration"/> section.</summary>
    public static IServiceCollection AddAndroidSubscriptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AndroidSubscriptionOptions>(configuration);
        return AddSubscriptionVerifierCore(services);
    }

    /// <summary>Registers <see cref="AndroidInAppPurchaseVerifier"/> with options binding.</summary>
    public static IServiceCollection AddAndroidInAppPurchases(
        this IServiceCollection services,
        Action<AndroidInAppPurchaseOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        return AddInAppPurchaseVerifierCore(services);
    }

    /// <summary>Binds <see cref="AndroidInAppPurchaseOptions"/> from an <see cref="IConfiguration"/> section.</summary>
    public static IServiceCollection AddAndroidInAppPurchases(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AndroidInAppPurchaseOptions>(configuration);
        return AddInAppPurchaseVerifierCore(services);
    }

    /// <summary>
    /// Registers the Android webhook parsers and authenticator. Always pair the authenticator with
    /// the parser — call <see cref="IWebhookAuthenticator.ValidateAsync"/> before
    /// <see cref="IWebhookParser.ParseAsync"/>.
    /// </summary>
    public static IServiceCollection AddAndroidWebhooks(
        this IServiceCollection services,
        Action<AndroidWebhookAuthenticatorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<AndroidWebhookAuthenticatorOptions>, AndroidWebhookAuthenticatorOptionsValidator>());
        services.AddSingleton<AndroidWebhookParser>();
        services.AddSingleton<IWebhookParser>(sp => sp.GetRequiredService<AndroidWebhookParser>());
        services.AddSingleton<AndroidInAppPurchaseWebhookParser>();
        services.AddSingleton<IInAppPurchaseWebhookParser>(sp => sp.GetRequiredService<AndroidInAppPurchaseWebhookParser>());
        services.AddSingleton(sp => new AndroidWebhookAuthenticator(
            sp.GetRequiredService<IOptions<AndroidWebhookAuthenticatorOptions>>()));
        services.AddSingleton<IWebhookAuthenticator>(sp => sp.GetRequiredService<AndroidWebhookAuthenticator>());
        return services;
    }

    private static IServiceCollection AddSubscriptionVerifierCore(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<AndroidSubscriptionOptions>, AndroidSubscriptionOptionsValidator>());
        services.AddSingleton(sp => new AndroidSubscriptionVerifier(
            sp.GetRequiredService<IOptions<AndroidSubscriptionOptions>>(),
            sp.GetRequiredService<ILogger<AndroidSubscriptionVerifier>>()));
        services.AddSingleton<ISubscriptionVerifier>(sp => sp.GetRequiredService<AndroidSubscriptionVerifier>());
        return services;
    }

    private static IServiceCollection AddInAppPurchaseVerifierCore(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<AndroidInAppPurchaseOptions>, AndroidInAppPurchaseOptionsValidator>());
        services.AddSingleton(sp => new AndroidInAppPurchaseVerifier(
            sp.GetRequiredService<IOptions<AndroidInAppPurchaseOptions>>(),
            sp.GetRequiredService<ILogger<AndroidInAppPurchaseVerifier>>()));
        services.AddSingleton<IInAppPurchaseVerifier>(sp => sp.GetRequiredService<AndroidInAppPurchaseVerifier>());
        return services;
    }
}
