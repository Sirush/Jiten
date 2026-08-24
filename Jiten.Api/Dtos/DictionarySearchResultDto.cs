using System.Text.Json.Serialization;

namespace Jiten.Api.Dtos;

public class DictionarySearchResultDto
{
    public string Query { get; set; } = "";
    public string QueryType { get; set; } = "";
    public List<DictionaryEntryDto> Results { get; set; } = [];
    public List<DictionaryEntryDto> DictionaryResults { get; set; } = [];
    public bool HasMore { get; set; }
}

public class DictionaryEntryDto
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string Text { get; set; } = "";
    public string RubyText { get; set; } = "";
    public string? PrimaryKanjiText { get; set; }
    public List<string> PartsOfSpeech { get; set; } = [];
    public List<string> Meanings { get; set; } = [];
    public int FrequencyRank { get; set; }

    /// <summary>Which ranking <see cref="FrequencyRank"/> came from; omitted while the caller is on the site-wide one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FrequencyRankSource { get; set; }

    /// <summary>Set only when a media-type default had no rank for the form and the global one stood in.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsFrequencyFallback { get; set; }
}
