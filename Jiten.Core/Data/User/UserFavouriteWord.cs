namespace Jiten.Core.Data;

public class UserFavouriteWord
{
    public const int MaxWords = 5;

    public string UserId { get; set; } = null!;
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public short Position { get; set; }
}
