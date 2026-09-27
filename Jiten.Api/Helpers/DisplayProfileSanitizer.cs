using System.Text.Json;
using System.Text.RegularExpressions;
using Jiten.Api.Dtos;

namespace Jiten.Api.Helpers;

/// <summary>Mirrors DISPLAY_VALUE_SCHEMA and MEDIA_CARD_STAT_IDS in Jiten.Web/app/utils/displayProfile.ts; both sides must gain a key together.</summary>
public static partial class DisplayProfileSanitizer
{
    public const int MaxProfiles = 10;
    public const int MaxNameLength = 40;
    public const int MediaCardColumnCount = 3;
    private const int MinReadingSpeed = 100;
    private const int MaxReadingSpeed = 100_000;
    private const int DifficultyBandCount = 6;

    private static readonly string[] StateColourKeys = ["new", "young", "due", "mature", "redundant", "ignored"];

    public static readonly IReadOnlyList<string> MediaCardStatIds =
    [
        "speechDuration", "characters", "wordCount", "uniqueWords", "uniqueKanji", "averageSentenceLength", "speechSpeed",
        "difficulty", "dialogue", "children", "runtime", "readingDuration", "externalRating", "appears",
    ];

    private static readonly HashSet<string> MediaCardStatIdSet = new(MediaCardStatIds, StringComparer.Ordinal);

    public static readonly IReadOnlyList<string> MediaCardSectionIds = ["description", "genres", "tags", "relations"];

    private static readonly Dictionary<string, Func<JsonElement, bool>> ValueRules = new(StringComparer.Ordinal)
    {
        ["titleLanguage"] = IntIn(0, 1, 2),
        ["themeMode"] = StringIn("light", "dark", "auto"),
        ["headwordFurigana"] = StringIn("shown", "unknown", "hidden"),
        ["sentenceFurigana"] = StringIn("all", "exceptTarget", "unknown", "off"),
        ["headwordSize"] = StringIn("sm", "md", "lg", "xl"),
        ["sentenceSize"] = StringIn("sm", "md", "lg", "xl"),
        ["japaneseFont"] = StringIn("default", "kyokasho", "serif", "rounded", "ud", "custom"),
        ["japaneseCustomFont"] = IsFontFamilyName,
        ["japaneseFontWordsOnly"] = IsBool,
        ["japaneseFontDictionaries"] = IsBool,
        ["furiganaSize"] = StringIn("sm", "md", "lg", "xl"),
        ["furiganaOnHover"] = IsBool,
        ["pitchAccentDisplay"] = StringIn("graph", "number", "both", "hidden"),
        ["pitchAccentColours"] = IsBool,
        ["colourWordsByState"] = IsBool,
        ["stateColours"] = IsStateColours,
        ["reducedMotion"] = StringIn("system", "always"),
        ["readingSpeed"] = IntBetween(MinReadingSpeed, MaxReadingSpeed),
        ["readingSpeedByDifficulty"] = IsBool,
        ["readingSpeeds"] = IsReadingSpeeds,
        ["displayAllNsfw"] = IsBool,
        ["hideVocabularyDefinitions"] = IsBool,
        ["hideCoverageBorders"] = IsBool,
        ["hideGenres"] = IsBool,
        ["hideTags"] = IsBool,
        ["hideRelations"] = IsBool,
        ["hideDescriptions"] = IsBool,
        ["hideExternalRating"] = IsBool,
        ["hideAlternativeTitles"] = IsBool,
        ["mediaCardSectionLayout"] = IsSectionLayout,
        ["quickMasterVocabulary"] = IsBool,
        ["ttsVoice"] = StringIn("female", "female2", "male", "male2", "asmr", "system", "random"),
        ["difficultyDisplayStyle"] = IntIn(0, 1, 2),
        ["difficultyValueDisplayStyle"] = IntIn(1, 2),
        ["difficultyPalette"] = StringIn("default", "redGreen", "blueYellow"),
        ["kanjiScale"] = StringIn("jlpt", "grade", "kanken", "wanikani", "rtk", "klc", "tmw", "none"),
        ["listView"] = IntIn(0, 1, 2),
    };

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex ProfileIdRegex();

    // Rendered inside a quoted CSS string, so quotes, backslashes and semicolons must never pass.
    [GeneratedRegex(@"^[\p{L}\p{N} _.\-]{0,80}$")]
    private static partial Regex FontFamilyNameRegex();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColourRegex();

    public static bool IsValidId(string? id) => id != null && ProfileIdRegex().IsMatch(id);

    public static string? SanitizeName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed;
    }

    public static Dictionary<string, JsonElement> SanitizeValues(Dictionary<string, JsonElement>? values)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (values == null) return result;

        foreach (var (key, value) in values)
        {
            if (ValueRules.TryGetValue(key, out var isValid) && isValid(value))
                result[key] = value.Clone();
        }

        return result;
    }

    /// <summary>Pads or trims to three columns and drops unknown ids and repeats; empty columns stay, since hiding every stat is a real choice.</summary>
    public static List<List<string>>? SanitizeStatColumns(List<List<string>>? columns)
    {
        if (columns == null) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return Enumerable.Range(0, MediaCardColumnCount)
                         .Select(i => (i < columns.Count ? columns[i] : null) ?? [])
                         .Select(column => column.Where(id => id != null && MediaCardStatIdSet.Contains(id) && seen.Add(id)).ToList())
                         .ToList();
    }

    /// <summary>A document written by an older or broken client degrades to "no profiles" rather than failing the read.</summary>
    public static List<DisplayProfileDto> Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json) || json == "{}") return [];
        try
        {
            var stored = JsonSerializer.Deserialize<DisplayProfilesDto>(json);
            return (stored?.Profiles ?? [])
                   .Where(p => IsValidId(p.Id))
                   .DistinctBy(p => p.Id)
                   .Take(MaxProfiles)
                   .Select(p => new DisplayProfileDto
                   {
                       Id = p.Id,
                       Name = SanitizeName(p.Name) ?? "Profile",
                       Values = SanitizeValues(p.Values),
                       StatColumns = SanitizeStatColumns(p.StatColumns),
                       UpdatedAt = p.UpdatedAt,
                   })
                   .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(List<DisplayProfileDto> profiles) =>
        JsonSerializer.Serialize(new DisplayProfilesDto { Profiles = profiles, MaxProfiles = MaxProfiles });

    private static Func<JsonElement, bool> StringIn(params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.Ordinal);
        return e => e.ValueKind == JsonValueKind.String && set.Contains(e.GetString()!);
    }

    private static Func<JsonElement, bool> IntIn(params int[] allowed) =>
        e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) && allowed.Contains(v);

    private static Func<JsonElement, bool> IntBetween(int min, int max) =>
        e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) && v >= min && v <= max;

    private static bool IsFontFamilyName(JsonElement e) =>
        e.ValueKind == JsonValueKind.String && FontFamilyNameRegex().IsMatch(e.GetString()!);

    private static bool IsBool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;

    /// <summary>One speed per difficulty band, Beginner to Insane.</summary>
    private static bool IsReadingSpeeds(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != DifficultyBandCount) return false;
        var inRange = IntBetween(MinReadingSpeed, MaxReadingSpeed);
        return e.EnumerateArray().All(inRange);
    }

    /// <summary>A "top" and a "bottom" list holding every section exactly once; hiding a section is its own toggle, never a missing entry.</summary>
    private static bool IsSectionLayout(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var zones = 0;
        foreach (var zone in e.EnumerateObject())
        {
            if (zone.Name is not ("top" or "bottom") || zone.Value.ValueKind != JsonValueKind.Array) return false;
            zones++;
            foreach (var item in zone.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || !MediaCardSectionIds.Contains(item.GetString()!) || !seen.Add(item.GetString()!))
                    return false;
            }
        }

        return zones == 2 && seen.Count == MediaCardSectionIds.Count;
    }

    /// <summary>Every state key must be present; null means the ordinary text colour.</summary>
    private static bool IsStateColours(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return false;
        var keys = 0;
        foreach (var property in e.EnumerateObject())
        {
            if (!StateColourKeys.Contains(property.Name)) return false;
            var v = property.Value;
            if (v.ValueKind != JsonValueKind.Null && !(v.ValueKind == JsonValueKind.String && HexColourRegex().IsMatch(v.GetString()!)))
                return false;
            keys++;
        }

        return keys == StateColourKeys.Length;
    }
}
