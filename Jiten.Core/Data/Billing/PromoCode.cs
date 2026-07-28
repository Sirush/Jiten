namespace Jiten.Core.Data.Billing;

public class PromoCode
{
    public int CodeId { get; set; }

    /// <summary>Redeemable code, 8-12 uppercase alphanumeric characters, unique.</summary>
    public required string Code { get; set; }

    /// <summary>Admin note, e.g. "Twitter giveaway Jan 2026".</summary>
    public string? Description { get; set; }

    /// <summary>Jiten+ days granted on redemption.</summary>
    public int DurationDays { get; set; }

    /// <summary>Null = unlimited uses.</summary>
    public int? MaxUses { get; set; }

    public int CurrentUses { get; set; }

    /// <summary>Null = never expires.</summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsActive { get; set; } = true;

    /// <summary>Public trial/giveaway codes grant Trial; compensation/contributor codes can grant Full.</summary>
    public bool GrantsFullTier { get; set; }
}
