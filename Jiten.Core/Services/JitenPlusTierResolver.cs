using Jiten.Core.Data.Billing;

namespace Jiten.Core.Services;

/// <summary>
/// Pure, ASP.NET-free resolution of a user's <see cref="JitenPlusTier"/> from their billing state.
/// Kept in Jiten.Core so the tier matrix is unit-testable without the API host.
/// </summary>
public static class JitenPlusTierResolver
{
    /// <summary>Grace window after a subscription's period end during which it still counts as active,
    /// covering the gap between a payment failure and Stripe's Smart Retries recovering it.</summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(3);

    public readonly record struct PromoCreditSnapshot(int RemainingDays, bool GrantsFullTier);

    public readonly record struct Input(
        bool StripeSubscriptionActive,
        DateTime? SubscriptionPeriodEnd,
        bool IsLifetime,
        bool AdminPremiumOverride,
        IReadOnlyList<PromoCreditSnapshot> Credits);

    /// <summary>
    /// A subscription counts as active while the Stripe flag is set, or — even after the flag flips
    /// false on a payment failure — while <c>now &lt; SubscriptionPeriodEnd + 3 days</c>.
    /// </summary>
    public static bool IsSubscriptionActive(in Input input, DateTime nowUtc) =>
        input.StripeSubscriptionActive
        || (input.SubscriptionPeriodEnd.HasValue && nowUtc < input.SubscriptionPeriodEnd.Value + GracePeriod);

    /// <summary>True while any unconsumed credit grants Full so a user never sits at Trial holding Full credit.</summary>
    public static bool HasFullCredit(in Input input) =>
        input.Credits != null && input.Credits.Any(c => c is { RemainingDays: > 0, GrantsFullTier: true });

    /// <summary>True while any unconsumed credit remains, regardless of tier.</summary>
    public static bool HasAnyCredit(in Input input) =>
        input.Credits != null && input.Credits.Any(c => c.RemainingDays > 0);

    public static JitenPlusTier Resolve(in Input input, DateTime nowUtc)
    {
        if (IsSubscriptionActive(input, nowUtc) || input.IsLifetime || input.AdminPremiumOverride)
            return JitenPlusTier.Full;

        if (HasFullCredit(input))
            return JitenPlusTier.Full;

        if (HasAnyCredit(input))
            return JitenPlusTier.Trial;

        return JitenPlusTier.None;
    }
}
