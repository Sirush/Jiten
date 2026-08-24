using System.Text.Json.Serialization;
using Jiten.Api.Helpers;

namespace Jiten.Api.Dtos;

public class WordFrequencyRanksDto
{
    public FrequencyRankEntryDto Global { get; set; } = new();

    /// <summary>Keyed by media type id; only the types that have observed this form appear.</summary>
    public Dictionary<int, FrequencyRankEntryDto> ByType { get; set; } = new();

    /// <summary>The caller's saved custom lists, rank 0 meaning the word is outside the list. Omitted for anonymous callers.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<FrequencyListRankDto>? Lists { get; set; }

    public ResolvedFrequencyRankDto Resolved { get; set; } = new();
}

public class FrequencyRankEntryDto
{
    public int Rank { get; set; }
    public double Percentage { get; set; }
    public int Amount { get; set; }
}

public class FrequencyListRankDto
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Rank { get; set; }
}

public class ResolvedFrequencyRankDto
{
    public string Source { get; set; } = FrequencyRankSources.Global;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MediaType { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ListId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ListName { get; set; }
    public int Rank { get; set; }
    public bool IsFallback { get; set; }
}
