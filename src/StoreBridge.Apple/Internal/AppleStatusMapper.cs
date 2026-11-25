namespace StoreBridge.Apple.Internal;

internal static class AppleStatusMapper
{
    /// <summary>
    /// Maps Apple's numeric subscription status. An active subscription whose auto-renew is off is
    /// reported as <see cref="SubscriptionStatus.Cancelled"/> (still entitled until it expires).
    /// </summary>
    internal static SubscriptionStatus Map(int? status, bool autoRenewOff = false) => status switch
    {
        1 when autoRenewOff => SubscriptionStatus.Cancelled,
        1 => SubscriptionStatus.Active,
        2 => SubscriptionStatus.Expired,
        3 => SubscriptionStatus.InBillingRetry,
        4 => SubscriptionStatus.InGracePeriod,
        5 => SubscriptionStatus.Revoked,
        _ => SubscriptionStatus.Unknown
    };
}
