namespace Jiten.Api.Dtos;

public class ResolveWordsRequest
{
    public List<ResolveWordPair> Pairs { get; set; } = new();

    /// <summary>Mirrors the vocabulary import's option: resolve conjugated surfaces through the parser.</summary>
    public bool ParseWords { get; set; }
}

public class ResolveWordPair
{
    public required string Word { get; set; }
    public string? Reading { get; set; }
}

public class ResolveWordsResponse
{
    public List<ResolvedWordDto> Resolved { get; set; } = new();
}

public class ResolvedWordDto
{
    public string Word { get; set; } = "";
    public string Reading { get; set; } = "";
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }

    /// <summary>
    /// Every writing of the word, primary first. A WordId's reading indexes are alternative writings of
    /// one reading (食べる/喰べる/たべる), so all of them are valid surfaces to search a sentence for.
    /// </summary>
    public List<string> Forms { get; set; } = new();
}

public class ImportExampleSentencesRequest
{
    public List<ImportExampleSentenceItem> Items { get; set; } = new();
}

public class ImportExampleSentenceItem
{
    public int Index { get; set; }
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }

    /// <summary>Plain text carrying at least one `**word**` marker; the client places and truncates it.</summary>
    public required string Text { get; set; }
    public string? Source { get; set; }
}

public class ImportExampleSentencesResponse
{
    public List<ImportExampleSentenceResult> Results { get; set; } = new();
    public int LimitPerWord { get; set; }
}

public class ImportExampleSentenceResult
{
    public int Index { get; set; }
    public required string Status { get; set; }
    public int? UserExampleSentenceId { get; set; }
}

public static class ImportExampleSentenceStatus
{
    public const string Ok = "ok";
    public const string Duplicate = "duplicate";
    public const string LimitReached = "limit_reached";
    public const string NoMarker = "no_marker";
    public const string TooLong = "too_long";
    public const string Invalid = "invalid";
}
