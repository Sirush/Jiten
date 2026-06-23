using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Jiten.Core.Data.JMDict;

public class JmDictWord
{
    public int WordId { get; set; }

    public List<string> PartsOfSpeech { get; set; } = new();
    public List<JmDictDefinition> Definitions { get; set; } = new();
    public List<JmDictLookup> Lookups { get; set; } = new();
    public List<int>? PitchAccents { get; set; } = new();
    public List<string>? Priorities { get; set; } = [];
    public WordOrigin Origin { get; set; } = WordOrigin.Unknown;
    public List<JmDictWordForm> Forms { get; set; } = [];

    /// <summary>lsource etymology / wasei info, stored as jsonb. Empty list when none.</summary>
    public string? LanguageSourcesJson { get; set; }

    /// <summary>Entry-level &lt;info&gt; notes, stored as jsonb. Empty list when none.</summary>
    public string? EntryInfoJson { get; set; }

    [NotMapped]
    public List<JmDictLanguageSource> LanguageSources
    {
        get => string.IsNullOrEmpty(LanguageSourcesJson)
            ? new List<JmDictLanguageSource>()
            : JsonSerializer.Deserialize<List<JmDictLanguageSource>>(LanguageSourcesJson) ?? new();
        set => LanguageSourcesJson = value is { Count: > 0 } ? JsonSerializer.Serialize(value) : null;
    }

    [NotMapped]
    public List<JmDictEntryInfo> EntryInfo
    {
        get => string.IsNullOrEmpty(EntryInfoJson)
            ? new List<JmDictEntryInfo>()
            : JsonSerializer.Deserialize<List<JmDictEntryInfo>>(EntryInfoJson) ?? new();
        set => EntryInfoJson = value is { Count: > 0 } ? JsonSerializer.Serialize(value) : null;
    }

    [NotMapped]
    private List<PartOfSpeech>? _cachedPartsOfSpeech;

    [NotMapped]
    public List<PartOfSpeech> CachedPOS => _cachedPartsOfSpeech ??= PartsOfSpeech.ToPartOfSpeech();

    [NotMapped]
    private uint? _cachedPOSMask;

    [NotMapped]
    public uint CachedPOSMask => _cachedPOSMask ??= PosMask.FromList(CachedPOS);

    [NotMapped]
    private bool? _isSuruVerb;

    [NotMapped]
    public bool IsSuruVerb => _isSuruVerb ??= PartsOfSpeech.Exists(p => p is "vs" or "vs-i" or "vs-s");

    [NotMapped]
    public bool IsFullyArchaic { get; set; }

    public int GetPriorityScore(bool isKana)
    {
        int score = 0;

        if (Priorities != null && Priorities.Any())
        {
            // Special priority for common words that get the wrong reading by default
            // i.e. 秋, 陽, etc
            if (Priorities.Contains("jiten"))
                score += 100;

            if (Priorities.Contains("ichi1"))
                score += 20;

            if (Priorities.Contains("ichi2"))
                score += 10;

            if (Priorities.Contains("news1"))
                score += 15;

            if (Priorities.Contains("news2"))
                score += 10;

            if (Priorities.Contains("gai1"))
                score += 15;

            if (Priorities.Contains("gai2"))
                score += 10;

            var nf = Priorities.FirstOrDefault(p => p.StartsWith("nf"));
            if (nf != null)
            {
                var nfRank = int.Parse(nf[2..]);
                score += Math.Max(0, 5 - (int)Math.Round(nfRank / 10f));
            }

            if (score == 0)
            {
                if (Priorities.Contains("spec1"))
                    score += 15;

                if (Priorities.Contains("spec2"))
                    score += 5;
            }
        }

        if (PartsOfSpeech.Contains("uk"))
        {
            if (isKana)
                score += 10;
            else
                score -= 10;
        }

        // Deprioritise names
        if (CachedPOS.Contains(PartOfSpeech.Name))
            score -= 50;

        return score;
    }
}
