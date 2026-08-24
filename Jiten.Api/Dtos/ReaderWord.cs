using System.Text.Json.Serialization;
using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class ReaderWord
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string Spelling { get; set; } = string.Empty;
    public string Reading { get; set; } = string.Empty;
    public int FrequencyRank { get; set; }
    public List<string> PartsOfSpeech { get; set; } = new();
    public List<List<string>> MeaningsChunks { get; set; } = new();
    public List<string> MeaningsPartOfSpeech { get; set; } = new();
    public List<KnownState> KnownState { get; set; } = new();
    public List<int> PitchAccents { get; set; } = new();
    public List<int> StudyDeckIds { get; set; } = new();

    /// <summary>Which ranking <see cref="FrequencyRank"/> came from; omitted while the caller is on the site-wide one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FrequencyRankSource { get; set; }

    /// <summary>Set only when a media-type default had no rank for the form and the global one stood in.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsFrequencyFallback { get; set; }
}