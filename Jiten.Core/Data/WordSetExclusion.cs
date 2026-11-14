namespace Jiten.Core.Data;

/// <summary>A form removed by hand from a rule-based set; regeneration never adds it back.</summary>
public class WordSetExclusion
{
    public int SetId { get; set; }
    public int WordId { get; set; }
    public short ReadingIndex { get; set; }

    public WordSet Set { get; set; } = null!;
}
