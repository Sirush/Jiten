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
    public int MaxSnippets { get; set; } = 50;
    /// <summary>Order the per-term results by descending occurrence count in the response and HTML export.</summary>
    public bool SortByOccurrence { get; set; } = true;
}

public class CorpusSearchResponse
{
    public List<CorpusTermResult> Results { get; set; } = [];
    public CorpusStats CorpusStats { get; set; } = new();
    /// <summary>Searchable decks/works/characters matching the active filters (year, difficulty, media type).</summary>
    public CorpusFilteredScope FilteredScope { get; set; } = new();
}

/// <summary>
/// The slice of the searchable corpus (decks with raw text) that the request's filters
/// (media type, difficulty, release year) select — the denominator the terms are searched within.
/// </summary>
public class CorpusFilteredScope
{
    public bool HasFilters { get; set; }
    public int Decks { get; set; }
    public int Works { get; set; }
    public long Characters { get; set; }
}

public class CorpusTermResult
{
    public required string Term { get; set; }
    /// <summary>Phrase forms subtracted from this term's matches (from "-exclusion" syntax in the term box),
    /// e.g. ヘアアクセ excluding ヘアアクセサリー. Each contains the term as a substring.</summary>
    public List<string> ExcludedTerms { get; set; } = [];
    public int MatchingDecks { get; set; }
    public long TotalOccurrences { get; set; }
    public double HitsPerMillion { get; set; }

    /// <summary>Distinct works (top-level series, collapsing sub-decks) containing the term.</summary>
    public int WorksMatched { get; set; }
    /// <summary>Total distinct works in the searchable corpus.</summary>
    public int WorksTotal { get; set; }
    /// <summary>WorksMatched as a percentage of WorksTotal — corpus range / document frequency.</summary>
    public double WorkRangePercentage { get; set; }
    /// <summary>Gries' Deviation of Proportions across media types: 0 = perfectly even spread, ~1 = concentrated in one register.</summary>
    public double Dispersion { get; set; }

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
    /// <summary>Plain-text version of the context (no highlight markup), for copyable citations / export.</summary>
    public required string Text { get; set; }
    public int DeckId { get; set; }
    public required string DeckTitle { get; set; }
    public string? ParentTitle { get; set; }
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
    /// <summary>Distinct works (top-level series) in the whole searchable corpus.</summary>
    public int TotalWorks { get; set; }
}

public class CorpusCoOccurrence
{
    public required string TermA { get; set; }
    public required string TermB { get; set; }
    public int SharedDecks { get; set; }
}
