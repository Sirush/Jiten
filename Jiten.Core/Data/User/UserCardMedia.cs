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
}
