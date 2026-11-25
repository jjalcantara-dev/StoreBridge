using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StoreBridge.Apple.Internal;

namespace StoreBridge.Apple;

/// <summary>
/// <see cref="IServiceCollection"/> extensions to register Apple App Store verifiers, parsers,
/// and the webhook authenticator with <c>IHttpClientFactory</c> integration.
/// </summary>
public static class AppleServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AppleSubscriptionVerifier"/> together with a named <see cref="HttpClient"/>.
    /// </summary>
    public static IServiceCollection AddAppleSubscriptions(
        this IServiceCollection services,
        Action<AppleSubscriptionOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        return AddSubscriptionVerifierCore(services);
    }

    /// <summary>Binds <see cref="AppleSubscriptionOptions"/> from an <see cref="IConfiguration"/> section.</summary>
    public static IServiceCollection AddAppleSubscriptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AppleSubscriptionOptions>(configuration);
        return AddSubscriptionVerifierCore(services);
    }

    /// <summary>
    /// Registers <see cref="AppleInAppPurchaseVerifier"/> together with a named <see cref="HttpClient"/>.
    /// </summary>
    public static IServiceCollection AddAppleInAppPurchases(
        this IServiceCollection services,
        Action<AppleInAppPurchaseOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        return AddInAppPurchaseVerifierCore(services);
    }

    /// <summary>Binds <see cref="AppleInAppPurchaseOptions"/> from an <see cref="IConfiguration"/> section.</summary>
    public static IServiceCollection AddAppleInAppPurchases(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AppleInAppPurchaseOptions>(configuration);
        return AddInAppPurchaseVerifierCore(services);
    }

    /// <summary>
    /// Registers the Apple webhook parsers and authenticator. Always pair the authenticator with
    /// the parser — call <see cref="IWebhookAuthenticator.ValidateAsync"/> before
    /// <see cref="IWebhookParser.ParseAsync"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">
    /// Configures the app identity notifications must belong to; <see cref="AppleWebhookAuthenticatorOptions.BundleId"/> is required.
    /// </param>
    public static IServiceCollection AddAppleWebhooks(
        this IServiceCollection services,
        Action<AppleWebhookAuthenticatorOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        return AddWebhooksCore(services);
    }

    /// <summary>Binds <see cref="AppleWebhookAuthenticatorOptions"/> from an <see cref="IConfiguration"/> section.</summary>
    public static IServiceCollection AddAppleWebhooks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<AppleWebhookAuthenticatorOptions>(configuration);
        return AddWebhooksCore(services);
    }

    private static IServiceCollection AddWebhooksCore(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<AppleWebhookAuthenticatorOptions>, AppleWebhookAuthenticatorOptionsValidator>());
        services.TryAddSingleton<AppleWebhookParser>();
        TryAddForwardedSingleton<IWebhookParser, AppleWebhookParser>(services);
        services.TryAddSingleton<AppleInAppPurchaseWebhookParser>();
        TryAddForwardedSingleton<IInAppPurchaseWebhookParser, AppleInAppPurchaseWebhookParser>(services);
        // Anchors to the built-in Apple Root CA - G3.
        services.TryAddSingleton(sp => new AppleWebhookAuthenticator(
            sp.GetRequiredService<IOptions<AppleWebhookAuthenticatorOptions>>()));
        TryAddForwardedSingleton<IWebhookAuthenticator, AppleWebhookAuthenticator>(services);
        return services;
    }

    private static IServiceCollection AddSubscriptionVerifierCore(IServiceCollection services)
    {
        services.AddHttpClient(AppleSubscriptionVerifier.HttpClientName);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<AppleSubscriptionOptions>, AppleSubscriptionOptionsValidator>());
        services.TryAddSingleton(sp => new AppleSubscriptionVerifier(
            sp.GetRequiredService<IOptions<AppleSubscriptionOptions>>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetService<ILogger<AppleSubscriptionVerifier>>()));
        TryAddForwardedSingleton<ISubscriptionVerifier, AppleSubscriptionVerifier>(services);
        return services;
    }

    private static IServiceCollection AddInAppPurchaseVerifierCore(IServiceCollection services)
    {
        services.AddHttpClient(AppleInAppPurchaseVerifier.HttpClientName);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<AppleInAppPurchaseOptions>, AppleInAppPurchaseOptionsValidator>());
        services.TryAddSingleton(sp => new AppleInAppPurchaseVerifier(
            sp.GetRequiredService<IOptions<AppleInAppPurchaseOptions>>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetService<ILogger<AppleInAppPurchaseVerifier>>()));
        TryAddForwardedSingleton<IInAppPurchaseVerifier, AppleInAppPurchaseVerifier>(services);
        return services;
    }

    // Typed Func<IServiceProvider, TImpl> preserves TImpl as the descriptor's implementation type so
    // TryAddEnumerable deduplicates repeated Apple registrations without colliding with Android's.
    private static void TryAddForwardedSingleton<TInterface, TImpl>(IServiceCollection services)
        where TInterface : class
        where TImpl : class, TInterface
    {
        Func<IServiceProvider, TImpl> factory = static sp => sp.GetRequiredService<TImpl>();
        services.TryAddEnumerable(new ServiceDescriptor(typeof(TInterface), factory, ServiceLifetime.Singleton));
    }
}
