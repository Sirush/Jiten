using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiten.Parser.Resolution;

// Ported Ichiran-style seg-filter rules (dict-grammar.lisp:defsegfilter).
// A seg-filter REJECTS a transition in the lattice: when segment B has wordId in
// `TargetWordIds`, AND the preceding segment A's wordId matches the rule's
// left-condition (either leftIs = must-match or leftIsNot = must-not-match),
// the transition is penalised with a large negative score — effectively pruning
// that path from the beam.
public static class SegFilters
{
    private static readonly Lazy<List<SegFilterRule>> _rules =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static IReadOnlyList<SegFilterRule> All => _rules.Value;

    private static List<SegFilterRule> Load()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "segfilters.json");
        if (!File.Exists(path)) return new List<SegFilterRule>();

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        var entries = JsonSerializer.Deserialize<List<SegFilterEntry>>(
            File.ReadAllText(path), options) ?? new();

        var rules = new List<SegFilterRule>(entries.Count);
        foreach (var e in entries)
        {
            var targets = e.TargetWordIds != null && e.TargetWordIds.Count > 0 ? new HashSet<int>(e.TargetWordIds) : null;
            var rightStarts = e.RightSurfaceStartsWith;
            var rightCompEnd = e.RightCompoundEndText;
            bool hasRightGate = targets != null
                || (rightStarts != null && rightStarts.Count > 0)
                || (rightCompEnd != null && rightCompEnd.Count > 0);
            if (!hasRightGate) continue;
            rules.Add(new SegFilterRule(
                Name: e.Name ?? string.Empty,
                TargetWordIds: targets,
                RightSurfaceStartsWith: rightStarts != null && rightStarts.Count > 0 ? rightStarts.ToArray() : null,
                RightCompoundEndText: e.RightCompoundEndText != null && e.RightCompoundEndText.Count > 0 ? e.RightCompoundEndText.ToArray() : null,
                LeftIs: e.LeftIs != null ? new HashSet<int>(e.LeftIs) : null,
                LeftIsNot: e.LeftIsNot != null ? new HashSet<int>(e.LeftIsNot) : null,
                LeftSurfaceEndsWith: e.LeftSurfaceEndsWith != null && e.LeftSurfaceEndsWith.Count > 0 ? e.LeftSurfaceEndsWith.ToArray() : null,
                LeftCompoundEndText: e.LeftCompoundEndText != null && e.LeftCompoundEndText.Count > 0 ? e.LeftCompoundEndText.ToArray() : null,
                LeftCompoundSeqIncludes: e.LeftCompoundSeqIncludes != null ? new HashSet<int>(e.LeftCompoundSeqIncludes) : null,
                LeftCompoundSeqExcludes: e.LeftCompoundSeqExcludes != null ? new HashSet<int>(e.LeftCompoundSeqExcludes) : null,
                LeftStemType: ParseStemType(e.LeftStemType),
                LeftConjType: e.LeftConjType,
                RightConjType: e.RightConjType,
                LeftHasNegative: e.LeftHasNegative,
                LeftHasFormal: e.LeftHasFormal,
                RightHasNegative: e.RightHasNegative,
                RightHasFormal: e.RightHasFormal));
        }
        return rules;
    }

    private static Suffixes.StemType? ParseStemType(string? s) => s?.ToLowerInvariant() switch
    {
        null or "" => null,
        "te-form"   => Suffixes.StemType.TeForm,
        "vs-noun"   => Suffixes.StemType.VsNoun,
        "masu-stem" => Suffixes.StemType.MasuStem,
        "neg-form"  => Suffixes.StemType.NegForm,
        "adj-stem"  => Suffixes.StemType.AdjStem,
        "adv-form"  => Suffixes.StemType.AdvForm,
        "sou-base"  => Suffixes.StemType.SouBase,
        _           => null
    };

    private sealed record SegFilterEntry(
        string? Name,
        [property: JsonPropertyName("targetWordIds")] IReadOnlyList<int>? TargetWordIds,
        [property: JsonPropertyName("rightSurfaceStartsWith")] IReadOnlyList<string>? RightSurfaceStartsWith,
        [property: JsonPropertyName("rightCompoundEndText")] IReadOnlyList<string>? RightCompoundEndText,
        [property: JsonPropertyName("leftIs")] IReadOnlyList<int>? LeftIs,
        [property: JsonPropertyName("leftIsNot")] IReadOnlyList<int>? LeftIsNot,
        [property: JsonPropertyName("leftSurfaceEndsWith")] IReadOnlyList<string>? LeftSurfaceEndsWith,
        [property: JsonPropertyName("leftCompoundEndText")] IReadOnlyList<string>? LeftCompoundEndText,
        [property: JsonPropertyName("leftCompoundSeqIncludes")] IReadOnlyList<int>? LeftCompoundSeqIncludes,
        [property: JsonPropertyName("leftCompoundSeqExcludes")] IReadOnlyList<int>? LeftCompoundSeqExcludes,
        [property: JsonPropertyName("leftStemType")] string? LeftStemType,
        [property: JsonPropertyName("leftConjType")] int? LeftConjType,
        [property: JsonPropertyName("rightConjType")] int? RightConjType,
        [property: JsonPropertyName("leftHasNegative")] bool? LeftHasNegative,
        [property: JsonPropertyName("leftHasFormal")] bool? LeftHasFormal,
        [property: JsonPropertyName("rightHasNegative")] bool? RightHasNegative,
        [property: JsonPropertyName("rightHasFormal")] bool? RightHasFormal,
        string? Description,
        string? Source);
}

// Within a rule, all specified (non-null) conditions must match for the transition
// to be rejected — AND semantics. Right-side conditions (TargetWordIds, RightSurfaceStartsWith)
// gate whether the rule applies at all; left-side conditions (LeftIs / LeftIsNot /
// LeftSurfaceEndsWith / LeftStemType) must all be satisfied too before rejection.
public readonly record struct SegFilterRule(
    string Name,
    HashSet<int>? TargetWordIds,
    string[]? RightSurfaceStartsWith,
    string[]? RightCompoundEndText,
    HashSet<int>? LeftIs,
    HashSet<int>? LeftIsNot,
    string[]? LeftSurfaceEndsWith,
    string[]? LeftCompoundEndText,
    HashSet<int>? LeftCompoundSeqIncludes,
    HashSet<int>? LeftCompoundSeqExcludes,
    Suffixes.StemType? LeftStemType,
    // Ichiran conj-type integer (see IchiranConjType). LeftConjType rejects if the
    // left segment's conjugation chain maps to this type; RightConjType gates on
    // the right/target segment's chain — needed for rules like sukiyoki where the
    // filter attacks the target in a specific conjugation form (adj-literary 好き).
    int? LeftConjType,
    int? RightConjType,
    // Structured conj-prop gates derived via ConjChainAnalysis (§Ichiran conj-prop
    // port). Non-null bool means the rule is only active when the analysed chain's
    // HasNegative / HasFormal flag matches. Lets rules say "reject suffix X after
    // a negative-form left" or "reject only when right is the formal variant",
    // mirroring the per-step neg/fml access Ichiran's suffix/synergy code uses.
    bool? LeftHasNegative,
    bool? LeftHasFormal,
    bool? RightHasNegative,
    bool? RightHasFormal);
