using System.ComponentModel.DataAnnotations;

namespace Jiten.Core.Data.MetadataEdits;

public class MetadataEdit
{
    public long Id { get; set; }
    public int DeckId { get; set; }

    public MetadataEditActor Actor { get; set; } = MetadataEditActor.User;

    /// <summary>Null for provider and system rows; identity lives in UserDbContext, so there is no FK.</summary>
    [MaxLength(450)]
    public string? UserId { get; set; }

    /// <summary>Set when <see cref="Actor"/> is Provider: which import wrote the row.</summary>
    public LinkType? Provider { get; set; }

    /// <summary>Shared by the rows one submission splits into; trust counts submissions, not rows.</summary>
    public Guid SubmissionId { get; set; }

    public MetadataEditStatus Status { get; set; } = MetadataEditStatus.Pending;
    public MetadataField Fields { get; set; }

    /// <summary>Per-field before/after pairs (<see cref="MetadataFieldChange"/>); the before half is what makes revert possible.</summary>
    public string ChangesJson { get; set; } = "[]";

    /// <summary>Up to 5 http(s) URLs.</summary>
    public string SourcesJson { get; set; } = "[]";

    [MaxLength(500)]
    public string? EditNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AppliedAt { get; set; }
    public DateTime? ReviewedAt { get; set; }

    [MaxLength(450)]
    public string? ReviewedByUserId { get; set; }

    [MaxLength(500)]
    public string? ReviewNote { get; set; }

    /// <summary>Set when this row is itself the revert of another edit.</summary>
    public long? RevertsEditId { get; set; }

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    public Deck? Deck { get; set; }
    public List<MetadataEditCheck> Checks { get; set; } = [];
}

public enum MetadataEditActor
{
    User = 1,
    Provider = 2,
    System = 3
}

public enum MetadataEditStatus
{
    Pending = 1,
    /// <summary>Auto-applied, not yet reviewed by anyone other than the author.</summary>
    Applied = 2,
    Approved = 3,
    Rejected = 4,
    Reverted = 5,
    Superseded = 6,
    /// <summary>Taken back by its author before review.</summary>
    Withdrawn = 7
}

/// <summary>One field of a <see cref="MetadataEdit"/>; values are the JSON forms produced by <c>DeckMetadataSnapshot</c>.</summary>
public sealed class MetadataFieldChange
{
    public MetadataField Field { get; set; }
    public System.Text.Json.JsonElement Before { get; set; }
    public System.Text.Json.JsonElement After { get; set; }
}
