using FluentAssertions;
using Jiten.Api.Services.Stripe;
using Jiten.Core.Data.Billing;
using Xunit;

namespace Jiten.Tests;

public class StripeUpgradeCreditTests
{
    private static readonly DateTime Now = new(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void YearlyMidTerm_IsHalfThePrice()
    {
        // 6 months (182.5 days) left of a €50 year → €25 credit, as in the plan's example (€150 − €25 = €125).
        var credit = StripeService.ComputeUpgradeCreditCents(SubscriptionPlan.Yearly, Now.AddDays(182.5), Now);
        credit.Should().Be(2500);
    }

    [Fact]
    public void MonthlyHalfTerm_IsHalfThePrice()
    {
        // 15 days left of a €5 month → €2.50 credit.
        var credit = StripeService.ComputeUpgradeCreditCents(SubscriptionPlan.Monthly, Now.AddDays(15), Now);
        credit.Should().Be(250);
    }

    [Fact]
    public void PeriodEndInThePast_IsZero()
    {
        StripeService.ComputeUpgradeCreditCents(SubscriptionPlan.Yearly, Now.AddDays(-1), Now).Should().Be(0);
    }

    [Fact]
    public void PeriodEndNull_IsZero()
    {
        StripeService.ComputeUpgradeCreditCents(SubscriptionPlan.Yearly, null, Now).Should().Be(0);
    }

    [Fact]
    public void FullTermRemaining_IsClampedToPrice()
    {
        // A full year (or more) remaining never exceeds the plan sticker price.
        StripeService.ComputeUpgradeCreditCents(SubscriptionPlan.Yearly, Now.AddDays(400), Now).Should().Be(5000);
    }

    [Fact]
    public void Proration_RoundsToWholeCents()
    {
        // 100 days of 365 of €50 = 1369.86… cents → rounds to 1370.
        var credit = StripeService.ComputeUpgradeCreditCents(SubscriptionPlan.Yearly, Now.AddDays(100), Now);
        credit.Should().Be(1370);
    }
}
