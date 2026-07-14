using FluentAssertions;
using Jiten.Api.Services.Stripe;
using Xunit;

namespace Jiten.Tests;

public class StripeUpgradeCreditTests
{
    private static readonly DateTime Now = new(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc);
    private const long LifetimeCents = 15000;

    private static StripeInvoiceRecord Inv(long paidCents, DateTime created, long refundedCents = 0) =>
        new(Guid.NewGuid().ToString("N"), paidCents, refundedCents, created);

    private static long Credit(IReadOnlyList<StripeInvoiceRecord> invoices, DateTime periodStart, DateTime periodEnd) =>
        StripeService.ComputeUpgradeCreditCents(invoices, periodStart, periodEnd, Now, LifetimeCents);

    // The bug regression test: a monthly subscriber who portal-switched to yearly paid only €5 + a small
    // proration (€4.25), never €50. Credit must reflect the ~€9.25 actually collected, not the €50 plan price.
    [Fact]
    public void UpgradeCredit_PlanSwitchWithoutFullPayment_CreditsOnlyActualPaid()
    {
        var periodStart = Now;                 // just switched → the short yearly period starts now
        var periodEnd = Now.AddDays(31);       // runs to the original Aug 14 billing anchor
        var invoices = new[]
        {
            Inv(500, Now),   // €5.00 monthly
            Inv(425, Now)    // €4.25 yearly proration
        };

        var credit = Credit(invoices, periodStart, periodEnd);

        credit.Should().Be(925);       // remainingFraction ≈ 1.0 × €9.25 net paid, capped at €9.25
        credit.Should().NotBe(5000);   // must NOT be the €50 plan price
    }

    // The plan's published example: 6 months into a genuinely-paid €50 year → €25 credit.
    [Fact]
    public void UpgradeCredit_LegitYearlyHalfElapsed_Credits25Euro()
    {
        var invoices = new[] { Inv(5000, Now.AddDays(-182.5)) };
        Credit(invoices, Now.AddDays(-182.5), Now.AddDays(182.5)).Should().Be(2500);
    }

    [Fact]
    public void UpgradeCredit_MonthlyMidCycle_Credits2Point50()
    {
        var invoices = new[] { Inv(500, Now.AddDays(-15)) };
        Credit(invoices, Now.AddDays(-15), Now.AddDays(15)).Should().Be(250);
    }

    // Older (prior-period) payments only raise the cap; only current-period payments drive the remaining value.
    [Fact]
    public void UpgradeCredit_RenewedYearly_OnlyCurrentPeriodPaymentsCount()
    {
        var invoices = new[]
        {
            Inv(5000, Now.AddDays(-547)),      // last year's payment — outside the current period
            Inv(5000, Now.AddDays(-182.5))     // current period's payment
        };
        Credit(invoices, Now.AddDays(-182.5), Now.AddDays(182.5)).Should().Be(2500);
    }

    [Fact]
    public void UpgradeCredit_RefundReducesNetPaid()
    {
        var invoices = new[] { Inv(5000, Now.AddDays(-182.5), refundedCents: 2000) }; // net €30 for the period
        Credit(invoices, Now.AddDays(-182.5), Now.AddDays(182.5)).Should().Be(1500);  // 0.5 × €30
    }

    [Fact]
    public void UpgradeCredit_FullyRefunded_IsZero()
    {
        var invoices = new[] { Inv(5000, Now.AddDays(-182.5), refundedCents: 5000) };
        Credit(invoices, Now.AddDays(-182.5), Now.AddDays(182.5)).Should().Be(0);
    }

    // A 100%-off promo subscription paid nothing → no credit, and the caller still proceeds at full price.
    [Fact]
    public void UpgradeCredit_NothingPaid_IsZero()
    {
        var invoices = new[] { Inv(0, Now.AddDays(-100)) };
        Credit(invoices, Now.AddDays(-182.5), Now.AddDays(182.5)).Should().Be(0);
    }

    [Fact]
    public void UpgradeCredit_NoInvoices_IsZero()
    {
        Credit([], Now.AddDays(-182.5), Now.AddDays(182.5)).Should().Be(0);
    }

    [Fact]
    public void UpgradeCredit_ClampedToLifetimePrice()
    {
        // Contrived over-payment for the period — credit can never exceed the €150 lifetime price.
        var invoices = new[] { Inv(20000, Now) };
        Credit(invoices, Now, Now.AddDays(365)).Should().Be(LifetimeCents);
    }

    [Fact]
    public void UpgradeCredit_PeriodEnded_IsZero()
    {
        var invoices = new[] { Inv(5000, Now.AddDays(-400)) };
        Credit(invoices, Now.AddDays(-400), Now.AddDays(-1)).Should().Be(0);
    }
}
