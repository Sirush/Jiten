using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiten.Core.Data.JMDict;

public enum DerivationVerdict
{
    Bidirectional,
    OneWayOnly,
    Exclude,
    ForceInclude,
    Recategorize
}

public class DerivationOverrideEntry
{
    [JsonPropertyName("baseWordId")] public int BaseWordId { get; set; }
    [JsonPropertyName("derivedWordId")] public int DerivedWordId { get; set; }
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = "";
    [JsonPropertyName("newCategory")] public string? NewCategory { get; set; }

    /// <summary>Kept across a Recategorize, which would otherwise reset a one-way pair to bidirectional.</summary>
    [JsonPropertyName("direction")] public string? Direction { get; set; }

    [JsonPropertyName("reason")] public string? Reason { get; set; }

    /// <summary>Free-text annotation from the first classification pass, replaced by verdict + newCategory.</summary>
    [JsonPropertyName("recategorize")] public string? Recategorize { get; set; }
}

public class DerivationOverrideFile
{
    [JsonPropertyName("overrides")] public List<DerivationOverrideEntry> Overrides { get; set; } = [];
}

public record DerivationOverride(DerivationVerdict Verdict, DerivationCategory? NewCategory,
                                 DerivationDirection? Direction = null);

/// <summary>Per-pair verdicts from <c>derivation_overrides.json</c>, keyed by (base, derived, category) so a
/// surface belonging to two categories is judged separately in each.</summary>
public class DerivationOverrideSet
{
    private readonly Dictionary<(int, int, DerivationCategory), DerivationOverride> _entries = new();

    public int Count => _entries.Count;
    public int UnknownCategoryCount { get; private set; }
    public int UnknownVerdictCount { get; private set; }
    public int LegacyRecategorizeCount { get; private set; }

    public bool TryGet(int baseWordId, int derivedWordId, DerivationCategory category, out DerivationOverride result)
        => _entries.TryGetValue((baseWordId, derivedWordId, category), out result!);

    public IEnumerable<(int BaseWordId, int DerivedWordId, DerivationCategory Category)> Keys =>
        _entries.Keys.Select(k => (k.Item1, k.Item2, k.Item3));

    /// <summary>Thrown rather than silently building an override-less table: without the file the mislabelled
    /// potentials the Recategorize entries park in a dormant category would ship conducting.</summary>
    public class MissingOverrideFileException(string message) : Exception(message);

    public static DerivationOverrideSet Load(string? path = null)
    {
        var set = new DerivationOverrideSet();
        path ??= ResolveResourcePath("derivation_overrides.json")
                 ?? throw new MissingOverrideFileException(
                     "derivation_overrides.json not found. Looked in: " + string.Join(", ", CandidatePaths("derivation_overrides.json")));

        if (!File.Exists(path))
            throw new MissingOverrideFileException($"derivation_overrides.json not found at {path}.");

        var file = JsonSerializer.Deserialize<DerivationOverrideFile>(File.ReadAllText(path));
        if (file == null)
            return set;

        foreach (var entry in file.Overrides)
        {
            if (entry.Recategorize != null)
            {
                set.LegacyRecategorizeCount++;
                Console.WriteLine($"  WARNING: override {entry.BaseWordId}→{entry.DerivedWordId} ({entry.Category}) " +
                                  $"still carries the legacy \"recategorize\" field: {entry.Recategorize}");
            }

            if (!DerivationCategories.TryParseKey(entry.Category, out var category))
            {
                set.UnknownCategoryCount++;
                continue;
            }

            if (!Enum.TryParse<DerivationVerdict>(entry.Verdict, ignoreCase: true, out var verdict))
            {
                set.UnknownVerdictCount++;
                continue;
            }

            DerivationCategory? newCategory = null;
            if (verdict == DerivationVerdict.Recategorize)
            {
                if (entry.NewCategory == null || !DerivationCategories.TryParseKey(entry.NewCategory, out var parsed))
                {
                    set.UnknownCategoryCount++;
                    continue;
                }

                newCategory = parsed;
            }

            var direction = ParseDirection(entry.Direction);

            set._entries[(entry.BaseWordId, entry.DerivedWordId, category)] = new(verdict, newCategory, direction);
        }

        return set;
    }

    /// <summary>Accepts the verdict spelling the classifier used ("OneWayOnly") beside the enum's own names.</summary>
    private static DerivationDirection? ParseDirection(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (string.Equals(value, nameof(DerivationVerdict.OneWayOnly), StringComparison.OrdinalIgnoreCase))
            return DerivationDirection.BaseToDerivedOnly;

        return Enum.TryParse<DerivationDirection>(value, ignoreCase: true, out var parsed) ? parsed : null;
    }

    private static string[] CandidatePaths(string fileName) =>
    [
        Path.Combine(AppContext.BaseDirectory, "resources", fileName),
        Path.Combine("Shared", "resources", fileName),
        Path.Combine("..", "Shared", "resources", fileName)
    ];

    private static string? ResolveResourcePath(string fileName) => CandidatePaths(fileName).FirstOrDefault(File.Exists);
}
