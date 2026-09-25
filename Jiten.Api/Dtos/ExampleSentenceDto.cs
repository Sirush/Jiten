namespace Jiten.Api.Dtos;

public class ExampleSentenceDto
{
    public long SentenceId { get; set; }
    public required string Text { get; set; }
    public int WordPosition { get; set; }
    public int WordLength { get; set; }
    public float Difficulty { get; set; }
    public StudyExampleSourceDto? SourceDeckParent { get; set; }
    public StudyExampleSourceDto? SourceDeck { get; set; }

    /// <summary>Set only by the authenticated study endpoint: the sentence comes from one of the caller's study decks.</summary>
    public bool FromStudyDeck { get; set; }

    /// <summary>Null for sentences parsed before token spans existed.</summary>
    public List<SentenceFuriganaDto>? Furigana { get; set; }
}

/// <summary>One ruby group over the sentence Text. Known is the caller's own state for the word and false when signed out.</summary>
public class SentenceFuriganaDto
{
    public int Position { get; set; }
    public int Length { get; set; }
    public string Reading { get; set; } = "";
    public int WordId { get; set; }
    public bool Known { get; set; }
}

public class ExampleSentencesByDifficultyResponse
{
    public float MinDifficulty { get; set; }
    public float MaxDifficulty { get; set; }
    public float SearchedBandMin { get; set; }
    public float SearchedBandMax { get; set; }
    public List<ExampleSentenceDto> Sentences { get; set; } = [];
} 