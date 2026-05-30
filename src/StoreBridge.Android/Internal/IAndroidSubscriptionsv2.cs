using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;

namespace StoreBridge.Android.Internal;

internal interface IAndroidSubscriptionsv2
{
    Task<SubscriptionPurchaseV2> GetAsync(string packageName, string purchaseToken, CancellationToken cancellationToken);
}

internal sealed class GoogleSubscriptionsv2Adapter : IAndroidSubscriptionsv2
{
    private readonly AndroidPublisherService _service;

    internal GoogleSubscriptionsv2Adapter(AndroidPublisherService service) => _service = service;

    public Task<SubscriptionPurchaseV2> GetAsync(string packageName, string purchaseToken, CancellationToken cancellationToken)
        => _service.Purchases.Subscriptionsv2.Get(packageName, purchaseToken).ExecuteAsync(cancellationToken);
}
