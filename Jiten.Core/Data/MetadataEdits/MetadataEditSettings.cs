namespace Jiten.Core.Data.MetadataEdits;

/// <summary>Single-row trust configuration (Id = 1); a missing row means these defaults.</summary>
public class MetadataEditSettings
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public int EstablishedMinAccountDays { get; set; } = 30;
    public int EstablishedCountedEdits { get; set; } = 15;
    public int EstablishedNoRevertDays { get; set; } = 90;
    public int TrustedCountedEdits { get; set; } = 50;
    public int TrustedNoRevertDays { get; set; } = 180;

    /// <summary>Highest <see cref="MetadataRiskTier"/> an Established user auto-applies; -1 means everything is queued.</summary>
    public int EstablishedAutoApplyMaxTier { get; set; } = -1;

    /// <summary>Highest <see cref="MetadataRiskTier"/> a Trusted user auto-applies; -1 means everything is queued.</summary>
    public int TrustedAutoApplyMaxTier { get; set; } = (int)MetadataRiskTier.Low;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Admin overrides for one contributor; absent row means no overrides.</summary>
public class MetadataContributor
{
    public required string UserId { get; set; }

    /// <summary>Admin-flagged Trusted regardless of record.</summary>
    public bool AdminTrusted { get; set; }

    /// <summary>Reverts at or before this moment no longer count toward the auto-block.</summary>
    public DateTime? BlockClearedAt { get; set; }

    /// <summary>Set by an admin for trolling; the account can't submit or check edits until it's lifted.</summary>
    public DateTime? BannedAt { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
