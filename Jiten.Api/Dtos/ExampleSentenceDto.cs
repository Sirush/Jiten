using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class ExampleSentenceDto
{
    public long SentenceId { get; set; }
    public required string Text { get; set; }
    public int WordPosition { get; set; }
    public int WordLength { get; set; }
    public float Difficulty { get; set; }
    public Deck? SourceDeckParent { get; set; }
    public Deck? SourceDeck { get; set; }

    /// <summary>Set only by the authenticated study endpoint: the sentence comes from one of the caller's study decks.</summary>
    public bool FromStudyDeck { get; set; }
}

public class ExampleSentencesByDifficultyResponse
{
    public float MinDifficulty { get; set; }
    public float MaxDifficulty { get; set; }
    public float SearchedBandMin { get; set; }
    public float SearchedBandMax { get; set; }
    public List<ExampleSentenceDto> Sentences { get; set; } = [];
} 