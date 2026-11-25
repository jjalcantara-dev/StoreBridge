using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;

namespace StoreBridge.Android.Internal;

/// <summary>Thin test seam over <c>purchases.productsv2.getproductpurchasev2</c>.</summary>
internal interface IAndroidProducts
{
    Task<ProductPurchaseV2> GetAsync(string packageName, string purchaseToken, CancellationToken cancellationToken);
}

internal sealed class GoogleProductsAdapter : IAndroidProducts
{
    private readonly AndroidPublisherService _service;

    internal GoogleProductsAdapter(AndroidPublisherService service) => _service = service;

    public Task<ProductPurchaseV2> GetAsync(string packageName, string purchaseToken, CancellationToken cancellationToken)
        => _service.Purchases.Productsv2.Getproductpurchasev2(packageName, purchaseToken).ExecuteAsync(cancellationToken);
}
