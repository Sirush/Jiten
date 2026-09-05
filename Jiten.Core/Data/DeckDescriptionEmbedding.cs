namespace Jiten.Core.Data;

/// <summary>Sentence embedding of a parent deck's description, used for natural-language media search.</summary>
public class DeckDescriptionEmbedding
{
    public int DeckId { get; set; }

    /// <summary>L2-normalised vector as little-endian float32 bytes.</summary>
    public byte[] Vector { get; set; } = [];

    /// <summary>SHA-256 of the embedded text; the sync job skips decks whose description hash is unchanged.</summary>
    public string TextHash { get; set; } = string.Empty;

    /// <summary>Model identifier the vector was produced with; vectors from a different model are recomputed.</summary>
    public string Model { get; set; } = string.Empty;
}
