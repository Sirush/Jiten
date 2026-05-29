namespace Jiten.Api.Dtos;

public class KanjiDto
{
    public string Character { get; set; } = "";
    public List<string> OnReadings { get; set; } = [];
    public List<string> KunReadings { get; set; } = [];
    public List<string> Meanings { get; set; } = [];
    public short StrokeCount { get; set; }
    public short? JlptLevel { get; set; }
    public short? Grade { get; set; }
    public int? FrequencyRank { get; set; }
    public List<WordSummaryDto>? TopWords { get; set; }
    public List<KanjiReadingWordsDto>? WordsByReading { get; set; }
}

public class KanjiReadingWordsDto
{
    public string Reading { get; set; } = "";
    public int TotalWords { get; set; }
    public List<WordSummaryDto> Words { get; set; } = [];
}

public class WordSummaryDto
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string Reading { get; set; } = "";
    public string ReadingFurigana { get; set; } = "";
    public string? MainDefinition { get; set; }
    public int? FrequencyRank { get; set; }
    public string? MatchSurface { get; set; }
}

public class KanjiListDto
{
    public string Character { get; set; } = "";
    public List<string> Meanings { get; set; } = [];
    public short StrokeCount { get; set; }
    public short? JlptLevel { get; set; }
    public int? FrequencyRank { get; set; }
}

public class KanjiGridItemDto
{
    public string Character { get; set; } = "";
    public int? FrequencyRank { get; set; }
    public short? JlptLevel { get; set; }
    public short? Grade { get; set; }
    public short StrokeCount { get; set; }
    public double Score { get; set; }
    public int WordCount { get; set; }
    public List<KanjiGridReadingDto>? Readings { get; set; }
}

public class KanjiGridReadingDto
{
    public string Reading { get; set; } = "";
    public int Known { get; set; }
    public int Required { get; set; }
    public double Weight { get; set; }
}

public class KanjiGridResponseDto
{
    public List<KanjiGridItemDto> Kanji { get; set; } = [];
    public int TotalKanjiCount { get; set; }
    public int SeenKanjiCount { get; set; }
    public DateTimeOffset? LastComputedAt { get; set; }
}
