namespace StoreBridge;

/// <summary>Helpers for <see cref="SubscriptionStatus"/>.</summary>
public static class SubscriptionStatusExtensions
{
    /// <summary>
    /// Whether a subscription in this status should currently grant access to paid content:
    /// <see cref="SubscriptionStatus.Active"/>, <see cref="SubscriptionStatus.Cancelled"/> (auto-renew off,
    /// still inside the paid period) and <see cref="SubscriptionStatus.InGracePeriod"/>.
    /// </summary>
    public static bool GrantsAccess(this SubscriptionStatus status) => status is
        SubscriptionStatus.Active or
        SubscriptionStatus.Cancelled or
        SubscriptionStatus.InGracePeriod;
}
