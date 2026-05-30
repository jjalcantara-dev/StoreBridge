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
    public static IServiceCollection AddAppleWebhooks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<AppleWebhookParser>();
        services.AddSingleton<IWebhookParser>(sp => sp.GetRequiredService<AppleWebhookParser>());
        services.AddSingleton<AppleInAppPurchaseWebhookParser>();
        services.AddSingleton<IInAppPurchaseWebhookParser>(sp => sp.GetRequiredService<AppleInAppPurchaseWebhookParser>());
        // Explicit factory: the parameterless ctor anchors to the built-in Apple Root CA - G3.
        services.AddSingleton(_ => new AppleWebhookAuthenticator());
        services.AddSingleton<IWebhookAuthenticator>(sp => sp.GetRequiredService<AppleWebhookAuthenticator>());
        return services;
    }

    private static IServiceCollection AddSubscriptionVerifierCore(IServiceCollection services)
    {
        services.AddHttpClient(AppleSubscriptionVerifier.HttpClientName);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<AppleSubscriptionOptions>, AppleSubscriptionOptionsValidator>());
        services.AddSingleton(sp => new AppleSubscriptionVerifier(
            sp.GetRequiredService<IOptions<AppleSubscriptionOptions>>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<AppleSubscriptionVerifier>>()));
        services.AddSingleton<ISubscriptionVerifier>(sp => sp.GetRequiredService<AppleSubscriptionVerifier>());
        return services;
    }

    private static IServiceCollection AddInAppPurchaseVerifierCore(IServiceCollection services)
    {
        services.AddHttpClient(AppleInAppPurchaseVerifier.HttpClientName);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<AppleInAppPurchaseOptions>, AppleInAppPurchaseOptionsValidator>());
        services.AddSingleton(sp => new AppleInAppPurchaseVerifier(
            sp.GetRequiredService<IOptions<AppleInAppPurchaseOptions>>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILogger<AppleInAppPurchaseVerifier>>()));
        services.AddSingleton<IInAppPurchaseVerifier>(sp => sp.GetRequiredService<AppleInAppPurchaseVerifier>());
        return services;
    }
}
