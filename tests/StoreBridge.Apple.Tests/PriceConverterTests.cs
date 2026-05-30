namespace StoreBridge.Apple.Tests;

public sealed class PriceConverterTests
{
    [Theory]
    [InlineData(1990, 1.99)]
    [InlineData(999, 0.999)]
    [InlineData(9990, 9.99)]
    [InlineData(0, 0)]
    [InlineData(1000, 1.0)]
    public void FromApplePrice_ConvertsCorrectly(long input, decimal expected)
    {
        Assert.Equal(expected, PriceConverter.FromApplePrice(input));
    }

    [Theory]
    [InlineData(1990000, 1.99)]
    [InlineData(9990000, 9.99)]
    [InlineData(0, 0)]
    [InlineData(1000000, 1.0)]
    public void FromGoogleMicros_ConvertsCorrectly(long input, decimal expected)
    {
        Assert.Equal(expected, PriceConverter.FromGoogleMicros(input));
    }
}
