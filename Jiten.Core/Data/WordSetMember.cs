namespace Jiten.Core.Data;

public class WordSetMember
{
    public int SetId { get; set; }
    public int WordId { get; set; }
    public short ReadingIndex { get; set; }
    public int Position { get; set; }

    /// <summary>Added by hand; regeneration keeps it whether or not the rule matches.</summary>
    public bool IsManual { get; set; }

    public WordSet Set { get; set; } = null!;
}
