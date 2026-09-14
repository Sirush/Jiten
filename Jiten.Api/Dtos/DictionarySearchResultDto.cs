using System.Text.Json.Serialization;
using Jiten.Core.Data.JMDict;

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
    public List<DictionarySenseDto> Senses { get; set; } = [];
    public int FrequencyRank { get; set; }

    /// <summary>Which ranking <see cref="FrequencyRank"/> came from; omitted while the caller is on the site-wide one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FrequencyRankSource { get; set; }

    /// <summary>Set only when a media-type default had no rank for the form and the global one stood in.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsFrequencyFallback { get; set; }
}

public class DictionarySenseDto
{
    public int Index { get; set; }
    public List<string> Meanings { get; set; } = [];
    public List<string> PartsOfSpeech { get; set; } = [];
    public List<string> Misc { get; set; } = [];

    public static List<DictionarySenseDto> FromDefinitions(IEnumerable<JmDictDefinition> definitions) =>
        definitions
            .Where(d => d.EnglishMeanings.Count > 0)
            .OrderBy(d => d.SenseIndex)
            .Select(d => new DictionarySenseDto
            {
                Index = d.SenseIndex,
                Meanings = d.EnglishMeanings,
                PartsOfSpeech = d.PartsOfSpeech,
                Misc = d.Misc,
            })
            .ToList();
}
