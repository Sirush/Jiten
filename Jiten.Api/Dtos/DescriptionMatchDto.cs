namespace Jiten.Api.Dtos;

/// <summary>A deck ranked by description similarity, carrying the full list-page DTO since describe mode renders every list view.</summary>
public class DescriptionMatchDto
{
    public DeckDto Deck { get; set; } = new();

    /// <summary>Cosine similarity of the dense embedding vectors (0-1).</summary>
    public float Similarity { get; set; }

    public int SimilarityPercent => (int)Math.Round(Similarity * 100);
}
