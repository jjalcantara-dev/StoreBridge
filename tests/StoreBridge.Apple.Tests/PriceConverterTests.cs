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

public sealed class SubscriptionStatusExtensionsTests
{
    [Theory]
    [InlineData(SubscriptionStatus.Active, true)]
    [InlineData(SubscriptionStatus.Cancelled, true)]
    [InlineData(SubscriptionStatus.InGracePeriod, true)]
    [InlineData(SubscriptionStatus.InBillingRetry, false)]
    [InlineData(SubscriptionStatus.Expired, false)]
    [InlineData(SubscriptionStatus.Revoked, false)]
    [InlineData(SubscriptionStatus.Unknown, false)]
    public void GrantsAccess_MatchesEntitlementSemantics(SubscriptionStatus status, bool expected) =>
        Assert.Equal(expected, status.GrantsAccess());
}
