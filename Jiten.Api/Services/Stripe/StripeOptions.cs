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
}
