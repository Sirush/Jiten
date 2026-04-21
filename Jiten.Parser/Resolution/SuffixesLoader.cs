using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiten.Parser.Resolution;

// Ported Ichiran-style suffix rules (dict-grammar.lisp:def-simple-suffix).
// A suffix rule says: when a stem of the given conjugation/POS class is
// followed by one of a small set of "attached" words (e.g. いる, する),
// the beam should consider them as a single combined segment with a bonus
// — i.e. keep them together at the segmentation level while still carrying
// the stem's WordId. Combined edges are injected into the beam lattice
// during Phase 2d; the bonus is additive to the node score.
public static class Suffixes
{
    public enum StemType
    {
        Unknown = 0,
        TeForm,   // stem ends in te-form (verb or i-adj).
        VsNoun,   // stem is a noun that can take する (JMDict vs / vs-s / vs-i / vs-c).
        MasuStem, // stem is the ren'youkei / masu-stem of a verb (食べ, し, 見, etc.).
        NegForm,  // stem is in negative form (〜ない / 〜なかった).
        AdjStem,  // i-less stem of an adj-i (楽し ← 楽しい, 高 ← 高い). For さ/がる/げ/め.
        AdvForm,  // adverbial form of an adj-i (高く ← 高い). For なる (become).
        SouBase,  // Ichiran suffix-sou-base: masu-stem OR adj-stem OR adverbial stem,
                  // with text-level reject list (な, よ, よさ, に, き) to block false positives.
        PastForm, // Ichiran suffix-rou: (find-word-with-conj-type root 2) — past tense.
                  // Root can be verb past (食べた) or adj-i past (赤かった) or copula past.
        Pronoun,  // Ichiran suffix-ra: root POS is pronoun (pn) OR root word is 1580640 (人).
                  // For pluralizer ら (おまえら, やつら, 人々ら).
    }

    private static readonly Lazy<List<SuffixRule>> _rules =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<SuffixRule> All => _rules.Value;

    private static List<SuffixRule> Load()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "suffixes.json");
        if (!File.Exists(path)) return new List<SuffixRule>();

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        var entries = JsonSerializer.Deserialize<List<SuffixEntry>>(
            File.ReadAllText(path), options) ?? new();

        var rules = new List<SuffixRule>(entries.Count);
        foreach (var e in entries)
        {
            if (e.AttachedWordIds == null || e.AttachedWordIds.Count == 0) continue;
            var stemType = e.StemType switch
            {
                "te-form"   => StemType.TeForm,
                "vs-noun"   => StemType.VsNoun,
                "masu-stem" => StemType.MasuStem,
                "neg-form"  => StemType.NegForm,
                "adj-stem"  => StemType.AdjStem,
                "adv-form"  => StemType.AdvForm,
                "sou-base"  => StemType.SouBase,
                "past-form" => StemType.PastForm,
                "pronoun"   => StemType.Pronoun,
                _           => StemType.Unknown
            };
            if (stemType == StemType.Unknown) continue;

            rules.Add(new SuffixRule(
                Name: e.Name ?? string.Empty,
                Stem: stemType,
                AttachedWordIds: new HashSet<int>(e.AttachedWordIds),
                Score: e.Score,
                StemStripScore: e.StemStripScore ?? e.Score,
                RequiresCompoundInLookup: e.RequiresCompoundInLookup ?? false,
                StemStrip: e.StemStrip ?? 0,
                UniqueOnly: e.UniqueOnly ?? false,
                StemBlacklistWordIds: e.StemBlacklistWordIds != null
                    ? new HashSet<int>(e.StemBlacklistWordIds)
                    : null,
                ScoreIsConstant: e.ScoreIsConstant ?? false,
                AttachedSurface: e.AttachedSurface,
                NoFurtherChain: e.NoFurtherChain ?? false,
                AllowPoliteAttached: e.AllowPoliteAttached ?? false,
                StemScoreOverrides: e.StemScoreOverrides,
                BridgeOnly: e.BridgeOnly ?? false));
        }
        return rules;
    }

    private sealed record SuffixEntry(
        string? Name,
        [property: JsonPropertyName("stemType")] string? StemType,
        [property: JsonPropertyName("attachedWordIds")] IReadOnlyList<int>? AttachedWordIds,
        int Score,
        [property: JsonPropertyName("stemStripScore")] int? StemStripScore,
        [property: JsonPropertyName("requiresCompoundInLookup")] bool? RequiresCompoundInLookup,
        [property: JsonPropertyName("stemStrip")] int? StemStrip,
        [property: JsonPropertyName("uniqueOnly")] bool? UniqueOnly,
        [property: JsonPropertyName("stemBlacklistWordIds")] IReadOnlyList<int>? StemBlacklistWordIds,
        [property: JsonPropertyName("scoreIsConstant")] bool? ScoreIsConstant,
        [property: JsonPropertyName("attachedSurface")] string? AttachedSurface,
        [property: JsonPropertyName("noFurtherChain")] bool? NoFurtherChain,
        [property: JsonPropertyName("allowPoliteAttached")] bool? AllowPoliteAttached,
        [property: JsonPropertyName("stemScoreOverrides")] Dictionary<string, int>? StemScoreOverrides,
        [property: JsonPropertyName("bridgeOnly")] bool? BridgeOnly,
        string? Description,
        string? Source);
}

// StemStrip: Ichiran's `:stem N` — the stem and attached overlap by N characters
// at the join boundary. Used by the chau/chimau/jau/jimau contractions where
// て〜しまう (4 chars) collapses into 〜ちゃう (3 chars). With StemStrip=1, the
// compound synth covers stemLen + attachedLen - 1 chars of input, preserving the
// stem's last char as the first char of the attached surface.
public readonly record struct SuffixRule(
    string Name,
    Suffixes.StemType Stem,
    HashSet<int> AttachedWordIds,
    int Score,
    // Score override for the BuildStemStripCompoundEdges path (Ichiran's :teiru class
    // for the contracted attached surfaces, vs :teiru+ for the full kana form). When
    // unspecified, defaults to Score so stem-strip and full-form share the value.
    int StemStripScore,
    bool RequiresCompoundInLookup = false,
    int StemStrip = 0,
    bool UniqueOnly = false,
    // Ichiran *suffix-unique-only* predicate — for :desu/:desho, suppress the
    // compound when the stem word's seq is in this set. Ports Ichiran's
    // "reject matches from じゃない conjugations" filter without the full
    // lisp predicate machinery.
    HashSet<int>? StemBlacklistWordIds = null,
    // Ichiran `apply-score-mod` distinguishes integer score_mods (SM × prop × len)
    // from function score_mods like `(constantly N)` which return a fixed N.
    // When true, the score is added additively, not multiplied with prop/length.
    // Used by kudasai (360), desu (200), desho (300), sou's cond-based score.
    bool ScoreIsConstant = false,
    // Ichiran `load-kf :text "..."`: when set, the rule only fires if the attached
    // candidate's matched surface equals this exact string. Used for the :rou class
    // which registers だろう's kana form under surface "ろう" specifically — without
    // this restriction the rule would also fire against the full "だろう" 3-char
    // edge, producing spurious compounds like 喜んだ+だろう.
    string? AttachedSurface = null,
    // When true, the synthesized compound cannot be used as a stem (scA) in the
    // second synthesis pass. Prevents spurious three-verb chains like
    // 近づいて来て (kuru pass-1) + いる (teiru pass-2) = 近づいて来ている.
    // Set on te+space endpoint rules (kuru, iku, oku, aru, oru, kureru, shimau, toku).
    bool NoFurtherChain = false,
    // When true, the rule accepts polite-bundled attached candidates (chain has "polite",
    // e.g. たいです). Default false: matches Ichiran's def-simple-suffix which checks
    // the dict form of the auxiliary. Enabling this for specific rules lets them merge
    // 観たいです / 回りたいです as single compounds. Pair with care — fixture expectations
    // in MorphologicalAnalyserTests typically expect adj-i+です split.
    bool AllowPoliteAttached = false,
    Dictionary<string, int>? StemScoreOverrides = null,
    // When true, the synthesized compound edge is used only as a bridge for
    // suffix-compound scoring — it is NOT exposed as a standalone lattice edge.
    // This prevents synthetic stems (e.g. 何か+ら → 何から, 申し訳な+さ → 申し訳なさ)
    // from appearing as high-scoring standalone segments that beat the correct
    // split path. The compound still participates in suffixCompounds scoring
    // so deeper chaining (e.g. 申し訳なさ+そう) works.
    bool BridgeOnly = false);
