namespace Jiten.Core.Data;

/// <summary>Parent-deck occurrences of one word form, packed as parallel arrays so a full coverage pass reads a few hundred MB instead of walking DeckWords.</summary>
public class WordParentDeckIndex
{
    public int WordId { get; set; }

    public byte ReadingIndex { get; set; }

    /// <summary>Parent DeckIds ascending; each position pairs with <see cref="Occurrences"/>.</summary>
    public int[] DeckIds { get; set; } = [];

    public int[] Occurrences { get; set; } = [];
}

/// <summary>Single-row marker of the last <see cref="WordParentDeckIndex"/> rebuild; parents updated after BuiltAt or missing from DeckIds are computed from DeckWords instead.</summary>
public class WordParentDeckIndexBuild
{
    /// <summary>Always 1.</summary>
    public int Id { get; set; }

    public DateTime BuiltAt { get; set; }

    /// <summary>Parent DeckIds the index was built from.</summary>
    public int[] DeckIds { get; set; } = [];
}
