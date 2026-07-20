using Jiten.Core.Data.Billing;

namespace Jiten.Api.Services.Stripe;

/// <summary>Bound from the <c>Stripe</c> config section. Keys live in sharedsettings.example.json.</summary>
public class StripeOptions
{
    public string SecretKey { get; set; } = "";
    public string WebhookSecret { get; set; } = "";
    public string MonthlyPriceId { get; set; } = "";
    public string YearlyPriceId { get; set; } = "";
    public string LifetimePriceId { get; set; } = "";

    /// <summary>Last day the lifetime option is purchasable. Rejected with 400 after this instant.</summary>
    public DateTime LifetimeWindowEnd { get; set; }

    /// <summary>Lifetime sticker price in whole cents (tax-inclusive) — the ceiling on any upgrade credit.</summary>
    public long LifetimePriceCents { get; set; } = 15000;

    /// <summary>Maps a Stripe price id to the local plan, or null if it matches neither subscription price.</summary>
    public SubscriptionPlan? PlanForPriceId(string? priceId)
    {
        if (string.IsNullOrEmpty(priceId)) return null;
        if (priceId == MonthlyPriceId) return SubscriptionPlan.Monthly;
        if (priceId == YearlyPriceId) return SubscriptionPlan.Yearly;
        return null;
    }

    /// <summary>A Stripe subscription status that grants access.</summary>
    public static bool IsActiveStatus(string? status) => status is "active" or "trialing";
}
