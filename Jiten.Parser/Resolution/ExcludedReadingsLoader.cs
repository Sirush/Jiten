using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiten.Parser.Resolution;

/// <summary>
/// Output-layer blocklist of (WordId, ReadingIndex) pairs that should never surface
/// in final parser results. See Shared/resources/excluded_readings.json for rationale
///   - rare-kana-reading-collides-with-grammar
///   - obscure-archaic-reading
///   - single-letter-or-symbol
///   - homograph-with-common-auxiliary
///   - legacy-uncategorized (pre-audit entries)
/// </summary>
public static class ExcludedReadings
{
    private static readonly Lazy<HashSet<(int WordId, byte ReadingIndex)>> _set =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static HashSet<(int WordId, byte ReadingIndex)> Set => _set.Value;

    public static bool Contains(int wordId, byte readingIndex) =>
        _set.Value.Contains((wordId, readingIndex));

    private static HashSet<(int WordId, byte ReadingIndex)> Load()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "excluded_readings.json");
        if (!File.Exists(path))
            return [];

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        var entries = JsonSerializer.Deserialize<List<ExcludedReadingEntry>>(
            File.ReadAllText(path), options) ?? [];

        var set = new HashSet<(int, byte)>(entries.Count);
        foreach (var e in entries)
            set.Add((e.WordId, e.ReadingIndex));
        return set;
    }

    private sealed record ExcludedReadingEntry(
        int WordId,
        byte ReadingIndex,
        [property: JsonPropertyName("surface")] string? Surface = null,
        [property: JsonPropertyName("word")] string? Word = null,
        [property: JsonPropertyName("reason")] string? Reason = null,
        [property: JsonPropertyName("notes")] string? Notes = null,
        [property: JsonPropertyName("addedOn")] string? AddedOn = null);
}
