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
        return AddWebhooksCore(services);
    }

    /// <summary>Binds <see cref="AndroidWebhookAuthenticatorOptions"/> from an <see cref="IConfiguration"/> section.</summary>
    public static IServiceCollection AddAndroidWebhooks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AndroidWebhookAuthenticatorOptions>(configuration);
        return AddWebhooksCore(services);
    }

    private static IServiceCollection AddWebhooksCore(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<AndroidWebhookAuthenticatorOptions>, AndroidWebhookAuthenticatorOptionsValidator>());
        services.TryAddSingleton<AndroidWebhookParser>();
        TryAddForwardedSingleton<IWebhookParser, AndroidWebhookParser>(services);
        services.TryAddSingleton<AndroidInAppPurchaseWebhookParser>();
        TryAddForwardedSingleton<IInAppPurchaseWebhookParser, AndroidInAppPurchaseWebhookParser>(services);
        services.TryAddSingleton(sp => new AndroidWebhookAuthenticator(
            sp.GetRequiredService<IOptions<AndroidWebhookAuthenticatorOptions>>()));
        TryAddForwardedSingleton<IWebhookAuthenticator, AndroidWebhookAuthenticator>(services);
        return services;
    }

    private static IServiceCollection AddSubscriptionVerifierCore(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<AndroidSubscriptionOptions>, AndroidSubscriptionOptionsValidator>());
        services.TryAddSingleton(sp => new AndroidSubscriptionVerifier(
            sp.GetRequiredService<IOptions<AndroidSubscriptionOptions>>(),
            sp.GetService<ILogger<AndroidSubscriptionVerifier>>()));
        TryAddForwardedSingleton<ISubscriptionVerifier, AndroidSubscriptionVerifier>(services);
        return services;
    }

    private static IServiceCollection AddInAppPurchaseVerifierCore(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<AndroidInAppPurchaseOptions>, AndroidInAppPurchaseOptionsValidator>());
        services.TryAddSingleton(sp => new AndroidInAppPurchaseVerifier(
            sp.GetRequiredService<IOptions<AndroidInAppPurchaseOptions>>(),
            sp.GetService<ILogger<AndroidInAppPurchaseVerifier>>()));
        TryAddForwardedSingleton<IInAppPurchaseVerifier, AndroidInAppPurchaseVerifier>(services);
        return services;
    }

    // Typed Func<IServiceProvider, TImpl> preserves TImpl as the descriptor's implementation type so
    // TryAddEnumerable deduplicates repeated Android registrations without colliding with Apple's.
    private static void TryAddForwardedSingleton<TInterface, TImpl>(IServiceCollection services)
        where TInterface : class
        where TImpl : class, TInterface
    {
        Func<IServiceProvider, TImpl> factory = static sp => sp.GetRequiredService<TImpl>();
        services.TryAddEnumerable(new ServiceDescriptor(typeof(TInterface), factory, ServiceLifetime.Singleton));
    }
}
