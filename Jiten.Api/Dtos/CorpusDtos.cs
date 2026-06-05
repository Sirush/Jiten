using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class CorpusSearchRequest
{
    public required List<string> Terms { get; set; }
    public List<MediaType>? MediaTypes { get; set; }
    public float? MinDifficulty { get; set; }
    public float? MaxDifficulty { get; set; }
    public int? MinReleaseYear { get; set; }
    public int? MaxReleaseYear { get; set; }
    public bool UseRegex { get; set; }
    public int MaxSnippets { get; set; } = 50;
}

public class CorpusSearchResponse
{
    public List<CorpusTermResult> Results { get; set; } = [];
    public CorpusStats CorpusStats { get; set; } = new();
}

public class CorpusTermResult
{
    public required string Term { get; set; }
    public int MatchingDecks { get; set; }
    public long TotalOccurrences { get; set; }
    public double HitsPerMillion { get; set; }
    public List<CorpusSnippet> Snippets { get; set; } = [];
    public List<CorpusMediaBreakdown> MediaBreakdown { get; set; } = [];
    public List<CorpusTrendPoint> Trends { get; set; } = [];
    public List<CorpusDifficultyBucket> DifficultyDistribution { get; set; } = [];
    public List<CorpusTopDeck> TopDecks { get; set; } = [];
    public double DialogueWeightedAvg { get; set; }
}

public class CorpusTopDeck
{
    public int DeckId { get; set; }
    public required string Title { get; set; }
    public string? ParentTitle { get; set; }
    public MediaType MediaType { get; set; }
    public int Occurrences { get; set; }
    public double PerMillion { get; set; }
}

public class CorpusSnippet
{
    public required string Html { get; set; }
    public int DeckId { get; set; }
    public required string DeckTitle { get; set; }
    public MediaType MediaType { get; set; }
    public float Difficulty { get; set; }
    public int ReleaseYear { get; set; }
}

public class CorpusMediaBreakdown
{
    public MediaType MediaType { get; set; }
    public int DeckCount { get; set; }
    public long TotalCharacters { get; set; }
    public long Occurrences { get; set; }
    public double HitsPerMillion { get; set; }
    public double Percentage { get; set; }
}

public class CorpusTrendPoint
{
    public int Year { get; set; }
    public long Occurrences { get; set; }
    public long TotalCharsInYear { get; set; }
    public double Percentage { get; set; }
}

public class CorpusDifficultyBucket
{
    public float BucketMin { get; set; }
    public float BucketMax { get; set; }
    public int DeckCount { get; set; }
}

public class CorpusStats
{
    public int TotalDecks { get; set; }
    public long TotalCharacters { get; set; }
    public int DecksWithRawText { get; set; }
}

public class CorpusCoOccurrence
{
    public required string TermA { get; set; }
    public required string TermB { get; set; }
    public int SharedDecks { get; set; }
}
