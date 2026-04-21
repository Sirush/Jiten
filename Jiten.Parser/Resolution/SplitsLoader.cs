using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiten.Parser.Resolution;

/// <summary>
/// How the rule's score-mod is applied. Mirrors Ichiran's calc-score split dispatch
/// (dict.lisp:939). Default handling depends on JITEN_SPLIT_ICHIRAN_MODE env — off
/// (Jiten legacy) preserves the original first-piece-bonus behaviour; on makes the
/// default Ichiran-plain (Replace), so unannotated entries match Ichiran semantics.
/// </summary>
public enum SplitMode
{
    /// <summary>Jiten legacy: score-mod added to the first piece's nodeScore so the
    /// split path gets an upfront bonus. Biases toward splitting.</summary>
    FirstPieceBonus,

    /// <summary>Ichiran :score — compound.nodeScore += score-mod. Bias direction
    /// follows the sign of score-mod (positive favours keeping the compound).</summary>
    Score,

    /// <summary>Ichiran :pscore — compound.prop-score += score-mod, then
    /// nodeScore = ceil(nodeScore × new-prop / old-prop). Non-linear adjustment that
    /// also scales the use-length bonus derived from prop-score.</summary>
    PScore,

    /// <summary>Ichiran plain (no flag): compound.nodeScore = score-mod + sum of
    /// piece nodeScores. Anchors the compound's attractiveness to its components
    /// plus a bonus; prevents the raw multiplicative score from over- or
    /// under-valuing the compound relative to its parts.</summary>
    Replace,
}

/// <summary>
/// Ported Ichiran-style per-WordId split rules (dict-split.lisp). When the beam's
/// lattice contains an edge for a WordId that has a split rule, the beam adds the
/// decomposition edges as an alternative path so it can pick the split when scoring
/// says to. Example: 1532270 「あけましておめでとうございます」 → [あけまして, おめでとうございます]
/// with +100 bonus.
///
/// Rules are pure data (Shared/resources/splits.json) — no C# per rule.
/// </summary>
public static class Splits
{
    private static readonly Lazy<Dictionary<int, SplitRule>> _map =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Dictionary<int, SplitRule> Map => _map.Value;

    // For compound-end checks (Ichiran: filter-is-compound-end-text).
    // Maps compound wordId → last piece's surface text. Only populated for
    // words that ARE compounds (have splits.json entries). Non-compound words
    // are absent, so a missing key means "not a compound" — compound-end
    // filters should not fire.
    public static Dictionary<int, string> CompoundEndTexts => _compoundEndTexts.Value;

    private static readonly Lazy<Dictionary<int, string>> _compoundEndTexts =
        new(() =>
        {
            var m = Map;
            var result = new Dictionary<int, string>(m.Count);
            foreach (var (wordId, rule) in m)
                if (rule.PieceTexts.Length > 0)
                    result[wordId] = rule.PieceTexts[^1];
            return result;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

    // Ichiran's filter-is-compound-end (and compound seq-set tests more generally)
    // require looking up a compound's component seqs. We build the map from
    // splits.json: compound wordId → set of piece wordIds (either the rule's
    // explicit per-piece wordId or the canonical lookup for the piece text).
    // Non-compounds are absent. The inner set is small (2–4 ints) so HashSet cost
    // is negligible.
    public static Dictionary<int, HashSet<int>> CompoundSeqSets => _compoundSeqSets.Value;

    // First-char index over rule.Text. Phase 1c-seq scans the sentence for
    // every rule's prefix text — with thousands of rules this is per-sentence
    // hot work. Bucketing rules by Text[0] lets the scan iterate only rules
    // whose prefix could plausibly match each char in the sentence.
    public static Dictionary<char, List<KeyValuePair<int, SplitRule>>> RulesByFirstChar =>
        _rulesByFirstChar.Value;

    private static readonly Lazy<Dictionary<char, List<KeyValuePair<int, SplitRule>>>> _rulesByFirstChar =
        new(() =>
        {
            var m = Map;
            var result = new Dictionary<char, List<KeyValuePair<int, SplitRule>>>();
            foreach (var kv in m)
            {
                if (string.IsNullOrEmpty(kv.Value.Text)) continue;
                char c = kv.Value.Text[0];
                if (!result.TryGetValue(c, out var list))
                    result[c] = list = new List<KeyValuePair<int, SplitRule>>();
                list.Add(kv);
            }
            return result;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<Dictionary<int, HashSet<int>>> _compoundSeqSets =
        new(() =>
        {
            var m = Map;
            var result = new Dictionary<int, HashSet<int>>(m.Count);
            foreach (var (wordId, rule) in m)
            {
                var set = new HashSet<int>();
                for (int i = 0; i < rule.PieceWordIds.Length; i++)
                {
                    if (rule.PieceWordIds[i].HasValue) set.Add(rule.PieceWordIds[i]!.Value);
                }
                if (set.Count > 0) result[wordId] = set;
            }
            return result;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool TryGet(int wordId, out SplitRule rule) =>
        _map.Value.TryGetValue(wordId, out rule!);

    // Default split mode for entries that don't specify "mode" in JSON. Ichiran's
    // plain-mode semantic (Replace) is faithful to dict.lisp:939: compound.nodeScore =
    // scoreMod + sum(piece.nodeScore). Measured +6 net vs the legacy first-piece-bonus
    // default on the parser-test suite. Escape hatch: set JITEN_SPLIT_ICHIRAN_MODE=0 to
    // restore the legacy default for entries authored before the Replace-default flip.
    private static readonly bool IchiranModeDefault =
        Environment.GetEnvironmentVariable("JITEN_SPLIT_ICHIRAN_MODE") != "0";

    private static Dictionary<int, SplitRule> Load()
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "splits.json");
        if (!File.Exists(path))
            return new Dictionary<int, SplitRule>();

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        var entries = JsonSerializer.Deserialize<List<SplitEntry>>(
            File.ReadAllText(path), options) ?? [];

        var defaultMode = IchiranModeDefault ? SplitMode.Replace : SplitMode.FirstPieceBonus;

        var map = new Dictionary<int, SplitRule>(entries.Count);
        foreach (var e in entries)
        {
            if (e.Split == null || e.Split.Count == 0) continue;
            // Validate pieces concatenate to the declared surface — guards against typos.
            var concat = string.Concat(e.Split.Select(p => p.Text));
            if (!string.IsNullOrEmpty(e.Text) && concat != e.Text) continue;

            var pieces = e.Split.Select(p => p.Text).ToArray();
            // Per-piece wordId overrides (nullable ints). Ichiran's def-simple-split
            // can specify an explicit seq for a piece (the "part-spec" integer or
            // (text seq) form) — we preserve it here so Phase 1c-seq can inject the
            // rewrite target into the lattice instead of deferring to lookups[text][0].
            // When absent, the beam falls back to the candidate provider's canonical
            // wordId for the piece surface.
            var pieceWordIds = e.Split.Select(p => p.WordId).ToArray();
            var mode = e.Mode switch
            {
                null or "" => defaultMode,
                "first-piece-bonus" or "firstPieceBonus" => SplitMode.FirstPieceBonus,
                "score" => SplitMode.Score,
                "pscore" => SplitMode.PScore,
                "replace" => SplitMode.Replace,
                _ => defaultMode,
            };

            map[e.WordId] = new SplitRule(
                WordId: e.WordId,
                Text: e.Text ?? concat,
                PieceTexts: pieces,
                PieceWordIds: pieceWordIds,
                Score: e.Score,
                Mode: mode,
                Conditions: e.Conditions ?? Array.Empty<string>());
        }
        return map;
    }

    private sealed record SplitEntry(
        int WordId,
        string? Text,
        List<SplitPiece>? Split,
        int Score,
        IReadOnlyList<string>? Conditions,
        [property: JsonPropertyName("mode")] string? Mode = null,
        [property: JsonPropertyName("source")] string? Source = null);

    private sealed record SplitPiece(string Text, int? WordId = null);
}

public readonly record struct SplitRule(
    int WordId,
    string Text,
    string[] PieceTexts,
    int?[] PieceWordIds,
    int Score,
    SplitMode Mode,
    IReadOnlyList<string> Conditions);
