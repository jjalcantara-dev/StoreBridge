namespace StoreBridge.Apple.Internal;

internal static class AppleStatusMapper
{
    internal static SubscriptionStatus Map(int? status) => status switch
    {
        1 => SubscriptionStatus.Active,
        2 => SubscriptionStatus.Expired,
        3 => SubscriptionStatus.InBillingRetry,
        4 => SubscriptionStatus.InGracePeriod,
        5 => SubscriptionStatus.Revoked,
        _ => SubscriptionStatus.Unknown
    };
}
