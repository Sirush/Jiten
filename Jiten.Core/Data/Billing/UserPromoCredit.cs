namespace Jiten.Core.Data.Billing;

public class UserPromoCredit
{
    public long UserPromoCreditId { get; set; }

    public string UserId { get; set; } = default!;

    public int PromoCodeId { get; set; }

    /// <summary>Decremented daily while the user has no active paid subscription.</summary>
    public int RemainingDays { get; set; }

    public DateTime GrantedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The last day a day was subtracted, so the decrement job runs at most once per day.</summary>
    public DateOnly? LastDecrementDate { get; set; }

    /// <summary>Set when RemainingDays reaches 0.</summary>
    public DateTime? FullyUsedAt { get; set; }

    /// <summary>Personal message shown to the recipient of an admin reward grant.</summary>
    public string? ThankYouMessage { get; set; }
}
