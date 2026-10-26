namespace Jiten.Core.Data;

/// <summary>A deck pair whose links an admin edited; provider imports never add links between them again.</summary>
public class DeckRelationshipOverride
{
    public int LowDeckId { get; set; }
    public int HighDeckId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public static (int Low, int High) Key(int a, int b) => a < b ? (a, b) : (b, a);
}
