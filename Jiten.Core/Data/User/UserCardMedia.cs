namespace Jiten.Core.Data.User;

public class UserCardMedia
{
    public long Id { get; set; }
    public string UserId { get; set; } = default!;
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public CardMediaKind Kind { get; set; }
    public string StoragePath { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public long FileSizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The file this row pointed at before the renormalize backfill rewrote it, kept on the CDN so the
    /// rewrite stays reversible. Null once discarded, and cleared whenever the media is replaced or deleted,
    /// which is what stops the superseded file from outliving anything that references it.
    /// </summary>
    public string? PreviousStoragePath { get; set; }

    public string? PreviousContentType { get; set; }
    public long? PreviousFileSizeBytes { get; set; }
}
