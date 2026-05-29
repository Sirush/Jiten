using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

public class UserKanjiGrid
{
    public string UserId { get; set; } = string.Empty;

    public string KanjiScoresJson { get; set; } = "{}";

    [NotMapped]
    private Dictionary<string, KanjiScoreEntry>? _cachedScores;

    [NotMapped]
    public Dictionary<string, KanjiScoreEntry> KanjiScores
    {
        get
        {
            try { return JsonSerializer.Deserialize<Dictionary<string, KanjiScoreEntry>>(KanjiScoresJson) ?? new(); }
            catch (JsonException) { return new(); }
        }
        set
        {
            KanjiScoresJson = JsonSerializer.Serialize(value);
            _cachedScores = null;
        }
    }

    public Dictionary<string, KanjiScoreEntry> GetKanjiScoresOnce()
    {
        if (_cachedScores != null) return _cachedScores;

        try
        {
            _cachedScores = JsonSerializer.Deserialize<Dictionary<string, KanjiScoreEntry>>(KanjiScoresJson) ?? new();
        }
        catch (JsonException)
        {
            _cachedScores = new();
        }

        return _cachedScores;
    }

    public DateTimeOffset LastComputedAt { get; set; }
}

public class KanjiScoreEntry
{
    [JsonPropertyName("s")] public double Score { get; set; }
    [JsonPropertyName("w")] public int WordCount { get; set; }
    [JsonPropertyName("r")] public List<ReadingEntry>? Readings { get; set; }
}

public class ReadingEntry
{
    [JsonPropertyName("r")] public string Reading { get; set; } = "";
    [JsonPropertyName("k")] public int Known { get; set; }
    [JsonPropertyName("q")] public int Required { get; set; }
    [JsonPropertyName("w")] public double Weight { get; set; }
}
