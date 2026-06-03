namespace Jiten.Api.Dtos;

/// <summary>
/// A deck recommended as semantically similar (FastText embeddings) to another deck.
/// </summary>
public class SimilarDeckDto
{
    public DeckDto Deck { get; set; } = new();

    /// <summary>Cosine similarity of the dense embedding vectors (0-1).</summary>
    public float Similarity { get; set; }

    /// <summary>Similarity rounded to a 0-100 percentage for display (derived from <see cref="Similarity"/>).</summary>
    public int SimilarityPercent => (int)Math.Round(Similarity * 100);
}
