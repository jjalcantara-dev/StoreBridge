using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;

namespace StoreBridge.Android.Internal;

internal interface IAndroidProducts
{
    Task<ProductPurchase> GetAsync(string packageName, string productId, string purchaseToken, CancellationToken cancellationToken);
}

internal sealed class GoogleProductsAdapter : IAndroidProducts
{
    private readonly AndroidPublisherService _service;

    internal GoogleProductsAdapter(AndroidPublisherService service) => _service = service;

    public Task<ProductPurchase> GetAsync(string packageName, string productId, string purchaseToken, CancellationToken cancellationToken)
        => _service.Purchases.Products.Get(packageName, productId, purchaseToken).ExecuteAsync(cancellationToken);
}
