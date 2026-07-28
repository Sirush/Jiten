using FluentAssertions;
using Jiten.Core.Data.Billing;
using Jiten.Core.Services;
using Xunit;

namespace Jiten.Tests;

public class JitenPlusTierResolverTests
{
    private static readonly DateTime Now = new(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);

    private static JitenPlusTierResolver.PromoCreditSnapshot Credit(int remainingDays, bool grantsFull) =>
        new(remainingDays, grantsFull);

    private static JitenPlusTierResolver.Input Input(
        bool subscriptionActive = false,
        DateTime? periodEnd = null,
        bool isLifetime = false,
        bool adminOverride = false,
        params JitenPlusTierResolver.PromoCreditSnapshot[] credits) =>
        new(subscriptionActive, periodEnd, isLifetime, adminOverride, credits);

    private static JitenPlusTier Resolve(JitenPlusTierResolver.Input input) =>
        JitenPlusTierResolver.Resolve(input, Now);

    // --- No entitlements ---

    [Fact]
    public void NoEntitlements_IsNone()
    {
        Resolve(Input()).Should().Be(JitenPlusTier.None);
    }

    // --- Each Full source in isolation ---

    [Fact]
    public void ActiveStripeSubscription_IsFull()
    {
        Resolve(Input(subscriptionActive: true)).Should().Be(JitenPlusTier.Full);
    }

    [Fact]
    public void Lifetime_IsFull()
    {
        Resolve(Input(isLifetime: true)).Should().Be(JitenPlusTier.Full);
    }

    [Fact]
    public void AdminOverride_IsFull()
    {
        Resolve(Input(adminOverride: true)).Should().Be(JitenPlusTier.Full);
    }

    [Fact]
    public void FullGrantingCredit_IsFull()
    {
        Resolve(Input(credits: Credit(5, grantsFull: true))).Should().Be(JitenPlusTier.Full);
    }

    // --- Grace window on the subscription period end ---

    [Fact]
    public void InactiveFlag_WithinGraceWindow_IsFull()
    {
        // Period ended 2 days ago; grace is 3 days, so still active despite the flag being false.
        Resolve(Input(subscriptionActive: false, periodEnd: Now.AddDays(-2))).Should().Be(JitenPlusTier.Full);
    }

    [Fact]
    public void InactiveFlag_JustInsideGraceBoundary_IsFull()
    {
        // now < periodEnd + 3d, by one second.
        Resolve(Input(subscriptionActive: false, periodEnd: Now.AddDays(-3).AddSeconds(1)))
            .Should().Be(JitenPlusTier.Full);
    }

    [Fact]
    public void InactiveFlag_ExactlyAtGraceBoundary_IsNone()
    {
        // now == periodEnd + 3d exactly → not strictly less than → expired.
        Resolve(Input(subscriptionActive: false, periodEnd: Now.AddDays(-3))).Should().Be(JitenPlusTier.None);
    }

    [Fact]
    public void InactiveFlag_OutsideGraceWindow_IsNone()
    {
        Resolve(Input(subscriptionActive: false, periodEnd: Now.AddDays(-4))).Should().Be(JitenPlusTier.None);
    }

    [Fact]
    public void NullPeriodEnd_InactiveFlag_IsNone()
    {
        Resolve(Input(subscriptionActive: false, periodEnd: null)).Should().Be(JitenPlusTier.None);
    }

    [Fact]
    public void ActiveFlag_WithFuturePeriodEnd_IsFull()
    {
        Resolve(Input(subscriptionActive: true, periodEnd: Now.AddDays(20))).Should().Be(JitenPlusTier.Full);
    }

    // --- Trial vs Full credit ---

    [Fact]
    public void TrialCreditOnly_IsTrial()
    {
        Resolve(Input(credits: Credit(6, grantsFull: false))).Should().Be(JitenPlusTier.Trial);
    }

    [Fact]
    public void ExhaustedCredit_IsNone()
    {
        // RemainingDays 0 → not counted at all.
        Resolve(Input(credits: Credit(0, grantsFull: true))).Should().Be(JitenPlusTier.None);
    }

    [Fact]
    public void ExhaustedTrialCredit_IsNone()
    {
        Resolve(Input(credits: Credit(0, grantsFull: false))).Should().Be(JitenPlusTier.None);
    }

    // --- Mixed credits ---

    [Fact]
    public void MixedCredits_FullPresent_IsFull()
    {
        // A Full credit alongside Trial credits keeps the user Full.
        Resolve(Input(credits: new[] { Credit(3, grantsFull: false), Credit(1, grantsFull: true) }))
            .Should().Be(JitenPlusTier.Full);
    }

    [Fact]
    public void MixedCredits_FullExhausted_TrialRemains_IsTrial()
    {
        // The only Full credit is spent; a Trial credit still has days → Trial.
        Resolve(Input(credits: new[] { Credit(0, grantsFull: true), Credit(4, grantsFull: false) }))
            .Should().Be(JitenPlusTier.Trial);
    }

    [Fact]
    public void MultipleTrialCredits_IsTrial()
    {
        Resolve(Input(credits: new[] { Credit(2, grantsFull: false), Credit(5, grantsFull: false) }))
            .Should().Be(JitenPlusTier.Trial);
    }

    // --- Combinations: a Full source always wins over credits ---

    [Fact]
    public void Subscription_OverridesTrialCredit_IsFull()
    {
        Resolve(Input(subscriptionActive: true, credits: Credit(6, grantsFull: false))).Should().Be(JitenPlusTier.Full);
    }

    [Fact]
    public void Lifetime_WithNoCredits_IsFull()
    {
        Resolve(Input(isLifetime: true, credits: Credit(0, grantsFull: false))).Should().Be(JitenPlusTier.Full);
    }

    [Fact]
    public void GraceWindow_WithTrialCredit_IsFull()
    {
        // In-grace subscription outranks a trial credit.
        Resolve(Input(subscriptionActive: false, periodEnd: Now.AddDays(-1), credits: Credit(3, grantsFull: false)))
            .Should().Be(JitenPlusTier.Full);
    }

    [Fact]
    public void ExpiredSubscription_WithTrialCredit_FallsBackToTrial()
    {
        // Subscription lapsed past grace, but a trial credit remains.
        Resolve(Input(subscriptionActive: false, periodEnd: Now.AddDays(-10), credits: Credit(3, grantsFull: false)))
            .Should().Be(JitenPlusTier.Trial);
    }

    [Fact]
    public void ExpiredSubscription_NoCredit_IsNone()
    {
        Resolve(Input(subscriptionActive: false, periodEnd: Now.AddDays(-10))).Should().Be(JitenPlusTier.None);
    }
}
