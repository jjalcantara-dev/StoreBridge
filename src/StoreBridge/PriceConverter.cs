namespace StoreBridge;

/// <summary>
/// Converts store-native price formats to a standard decimal.
/// </summary>
public static class PriceConverter
{
    /// <summary>
    /// Converts Apple's price (in thousandths) to a decimal.
    /// Apple always reports in thousandths: 1990 → 1.99, 990 → 0.99.
    /// </summary>
    public static decimal FromApplePrice(long priceInThousandths) =>
        priceInThousandths / 1000m;

    /// <summary>
    /// Converts Google's price (in micros) to a decimal.
    /// Google reports 1990000 for €1.99.
    /// </summary>
    public static decimal FromGoogleMicros(long priceInMicros) =>
        priceInMicros / 1_000_000m;
}
