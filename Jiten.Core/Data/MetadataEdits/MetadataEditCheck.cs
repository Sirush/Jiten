using System.ComponentModel.DataAnnotations;

namespace Jiten.Core.Data.MetadataEdits;

/// <summary>A contributor confirming or doubting someone else's pending edit; a signal for editors and a hidden input to trust.</summary>
public class MetadataEditCheck
{
    public long Id { get; set; }
    public long MetadataEditId { get; set; }

    [MaxLength(450)]
    public required string UserId { get; set; }

    public MetadataEditVerdict Verdict { get; set; }

    /// <summary>Doubts only: the changes that look wrong; None means the whole edit.</summary>
    public MetadataField Fields { get; set; }

    /// <summary>Required for Doubt; shown to editors only.</summary>
    public MetadataEditDoubtReason? Reason { get; set; }

    [MaxLength(300)]
    public string? Note { get; set; }

    [MaxLength(500)]
    public string? SourceUrl { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    public MetadataEdit? Edit { get; set; }
}

public enum MetadataEditVerdict
{
    Confirm = 1,
    Doubt = 2
}

public enum MetadataEditDoubtReason
{
    SourceDoesNotMatch = 1,
    WrongValue = 2,
    NoSource = 3,
    Other = 4
}
