using System.Text.RegularExpressions;

namespace Jiten.Api.Services;

public sealed record GlossSenseCandidate(int WordId, int SenseIndex, List<string> Meanings, List<string> Misc, List<string> GlossTypes, bool IsCommon = false);

public sealed record GlossSearchHit(int WordId, int SenseIndex, List<string> Meanings, double Score);

/// <summary>Ranks English-gloss candidates so an exact gloss beats a gloss that merely contains the query, and frequency only breaks ties.</summary>
public static class GlossSearchScorer
{
    private const double ExactTier = 1000;
    private const double PrefixTier = 700;
    private const double PhraseTier = 500;
    private const double CoverageTierWidth = 300;
    private const double SensePenalty = 20;
    private const double GlossPenalty = 5;
    private const double ObscureSensePenalty = 150;
    private const double NonPlainGlossPenalty = 50;
    private const double NounQueryVerbGlossPenalty = 40;
    private const double VerbQueryNounGlossPenalty = 120;
    private const double CommonWordBonus = 60;
    private const double FrequencyWeight = 40;
    private const int UnrankedFrequency = 1_000_000;

    private static readonly HashSet<string> Stopwords =
    [
        "a", "an", "the", "to", "of", "and", "or", "in", "on", "at", "for", "with", "by", "from", "as",
        "be", "is", "are", "was", "were", "been", "being", "it", "its", "this", "that", "these", "those",
        "all", "over", "into", "up", "out", "one", "ones", "oneself", "someone", "something", "somebody",
        "esp", "etc", "eg", "ie", "usu", "often", "also", "very", "so", "just", "not", "no",
    ];

    private static readonly HashSet<string> ObscureMisc = ["arch", "obs", "obsc", "rare", "sl", "derog", "vulg", "X", "dated"];

    private static readonly Regex Parenthetical = new(@"\([^()]*\)", RegexOptions.Compiled);
    private static readonly Regex Brackets = new(@"[()]", RegexOptions.Compiled);
    private static readonly Regex NonWord = new(@"[^a-z0-9' ]+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static List<GlossSearchHit> Rank(string query, IEnumerable<GlossSenseCandidate> candidates, IReadOnlyDictionary<int, int> frequencyRanks)
    {
        var (normalisedQuery, queryIsVerb) = Normalise(query);
        var queryTokens = ContentTokens(normalisedQuery);
        if (normalisedQuery.Length == 0) return [];

        var bestPerWord = new Dictionary<int, GlossSearchHit>();
        foreach (var sense in candidates)
        {
            var score = ScoreSense(sense, normalisedQuery, queryIsVerb, queryTokens);
            if (score is null) continue;

            var rank = frequencyRanks.GetValueOrDefault(sense.WordId, UnrankedFrequency);
            if (rank <= 0 || rank == int.MaxValue) rank = UnrankedFrequency;
            var total = score.Value - Math.Log10(rank) * FrequencyWeight + (sense.IsCommon ? CommonWordBonus : 0);

            if (!bestPerWord.TryGetValue(sense.WordId, out var existing) || total > existing.Score)
                bestPerWord[sense.WordId] = new GlossSearchHit(sense.WordId, sense.SenseIndex, sense.Meanings, total);
        }

        return bestPerWord.Values
            .OrderByDescending(h => h.Score)
            .ThenBy(h => frequencyRanks.GetValueOrDefault(h.WordId, UnrankedFrequency))
            .ThenBy(h => h.WordId)
            .ToList();
    }

    private static double? ScoreSense(GlossSenseCandidate sense, string query, bool queryIsVerb, string[] queryTokens)
    {
        double? best = null;
        for (var i = 0; i < sense.Meanings.Count; i++)
        {
            var glossScore = ScoreGloss(sense.Meanings[i], query, queryIsVerb, queryTokens);
            if (glossScore is null) continue;

            var score = glossScore.Value - Math.Min(i, 10) * GlossPenalty;
            if (i < sense.GlossTypes.Count && sense.GlossTypes[i] is "expl" or "lit")
                score -= NonPlainGlossPenalty;
            if (best is null || score > best) best = score;
        }

        if (best is null) return null;
        best -= Math.Min(sense.SenseIndex, 10) * SensePenalty;
        if (sense.Misc.Any(ObscureMisc.Contains)) best -= ObscureSensePenalty;
        return best;
    }

    /// <summary>Best of the gloss with parentheticals dropped and the gloss with only the brackets dropped, so "to put (it all) together" answers both "put together" and "put it all together".</summary>
    private static double? ScoreGloss(string gloss, string query, bool queryIsVerb, string[] queryTokens)
    {
        var (stripped, glossIsVerb) = Normalise(gloss);
        var (flattened, _) = Normalise(gloss, keepParentheticalText: true);
        var a = ScoreText(stripped, query, queryTokens);
        var b = stripped == flattened ? null : ScoreText(flattened, query, queryTokens);
        double? best = a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
        if (best is not null && glossIsVerb != queryIsVerb)
            best -= queryIsVerb ? VerbQueryNounGlossPenalty : NounQueryVerbGlossPenalty;
        return best;
    }

    private static double? ScoreText(string text, string query, string[] queryTokens)
    {
        if (text.Length == 0) return null;
        if (text == query) return ExactTier;
        if (text.StartsWith(query + " ", StringComparison.Ordinal)) return PrefixTier;
        if ((" " + text + " ").Contains(" " + query + " ", StringComparison.Ordinal)) return PhraseTier;

        if (queryTokens.Length == 0) return null;
        var textTokens = ContentTokens(text);
        if (textTokens.Length == 0) return null;

        var textStems = textTokens.Select(Stem).ToHashSet();
        var matched = queryTokens.Count(t => textStems.Contains(Stem(t)));
        if (matched == 0) return null;

        var queryCoverage = (double)matched / queryTokens.Length;
        var glossCoverage = (double)matched / textStems.Count;
        return CoverageTierWidth * (0.6 * queryCoverage + 0.4 * glossCoverage);
    }

    /// <summary>Lowercased, punctuation-free text with a leading "to" removed; the flag reports whether that infinitive marker was there.</summary>
    public static (string Text, bool IsVerbForm) Normalise(string text, bool keepParentheticalText = false)
    {
        var s = text.ToLowerInvariant();
        if (keepParentheticalText)
        {
            s = Brackets.Replace(s, " ");
        }
        else
        {
            string previous;
            do { previous = s; s = Parenthetical.Replace(s, " "); } while (s != previous);
        }
        s = NonWord.Replace(s, " ");
        s = Spaces.Replace(s, " ").Trim();
        var isVerbForm = s.StartsWith("to ", StringComparison.Ordinal);
        if (isVerbForm) s = s[3..];
        return (s, isVerbForm);
    }

    private static string[] ContentTokens(string normalised) =>
        normalised.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => !Stopwords.Contains(t)).ToArray();

    /// <summary>Crude suffix stemmer; Postgres does the real stemming at match time, this only serves coverage scoring.</summary>
    private static string Stem(string token)
    {
        if (token.Length <= 3) return token;
        foreach (var suffix in new[] { "ing", "ies", "ied", "ed", "es", "s" })
        {
            if (token.EndsWith(suffix, StringComparison.Ordinal) && token.Length - suffix.Length >= 3)
            {
                var stem = token[..^suffix.Length];
                return suffix is "ies" or "ied" ? stem + "y" : stem;
            }
        }
        return token;
    }
}
