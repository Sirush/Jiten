namespace Jiten.Core.Data.Billing;

public enum PromoCreditSource
{
    /// <summary>The user redeemed a promo code.</summary>
    Redemption = 0,

    /// <summary>An admin granted the credit directly (no code); may carry a thank-you message.</summary>
    AdminGrant = 1
}
