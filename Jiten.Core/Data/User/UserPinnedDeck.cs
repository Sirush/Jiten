namespace Jiten.Core.Data;

/// <summary>A title the user chose to show on their profile, separate from the private favourite flag.</summary>
public class UserPinnedDeck
{
    public const int MaxPins = 6;

    public string UserId { get; set; } = null!;
    public int DeckId { get; set; }
    public short Position { get; set; }
}
