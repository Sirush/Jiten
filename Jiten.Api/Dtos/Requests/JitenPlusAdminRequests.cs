using System.ComponentModel.DataAnnotations;

namespace Jiten.Api.Dtos.Requests;

/// <summary>Grant Jiten+ to a user directly (a reward, not a code redemption).</summary>
public class GrantJitenPlusRequest
{
    [Required]
    public required string UserIdOrName { get; set; }

    /// <summary>"days" or "lifetime".</summary>
    [Required]
    public required string Kind { get; set; }

    /// <summary>Required when Kind == "days".</summary>
    public int? Days { get; set; }

    /// <summary>Grants default to Full tier — they are rewards.</summary>
    public bool GrantsFullTier { get; set; } = true;

    [StringLength(1000)]
    public string? ThankYouMessage { get; set; }
}

/// <summary>Revoke a mistakenly-granted contributor lifetime (never a purchased one).</summary>
public class RevokeLifetimeRequest
{
    [Required]
    public required string UserIdOrName { get; set; }
}

public class CreatePromoCodeRequest
{
    /// <summary>Optional; a code is generated when left blank.</summary>
    [StringLength(12, MinimumLength = 8)]
    public string? Code { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    [Range(1, 100000)]
    public int DurationDays { get; set; }

    [Range(1, int.MaxValue)]
    public int? MaxUses { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public bool GrantsFullTier { get; set; }
}

public class UpdatePromoCodeRequest
{
    [StringLength(500)]
    public string? Description { get; set; }

    public int? MaxUses { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public bool? IsActive { get; set; }

    public bool? GrantsFullTier { get; set; }
}

public class BulkGeneratePromoCodesRequest
{
    [Range(1, 1000)]
    public int Count { get; set; }

    [StringLength(500)]
    public string? Description { get; set; }

    [Range(1, 100000)]
    public int DurationDays { get; set; }

    [Range(1, int.MaxValue)]
    public int? MaxUses { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public bool GrantsFullTier { get; set; }
}
