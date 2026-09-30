namespace Jiten.Core.Data;

/// <summary>Per-deck content-word sets of every sentence, packed by <see cref="SentenceProfileCodec"/>.</summary>
public class DeckSentenceProfile
{
    public int DeckId { get; set; }

    /// <summary>Every sentence of the deck's own text; null for decks whose text lives in their children.</summary>
    public byte[]? Profile { get; set; }

    public int SentenceCount { get; set; }

    /// <summary>Evenly spaced sentences across the whole work, set on top-level decks only; what the coverage job reads.</summary>
    public byte[]? Sample { get; set; }

    public int SampleCount { get; set; }

    public short Version { get; set; }

    public DateTime BuiltAt { get; set; }
}
