namespace Jiten.Core.Data.User;

public class UserCustomMeaning
{
    public int UserCustomMeaningId { get; set; }
    public string UserId { get; set; } = default!;
    public int WordId { get; set; }
    public required string Text { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
