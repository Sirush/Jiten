namespace Jiten.Core.Data;

public class MediaRequestBoost
{
    public int Id { get; set; }
    public int MediaRequestId { get; set; }
    public required string UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MediaRequest? MediaRequest { get; set; }
}
