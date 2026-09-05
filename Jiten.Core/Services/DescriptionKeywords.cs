using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jiten.Core.Services;

/// <summary>
/// Lexical side of description search. Dense embeddings miss rare proper nouns ("onmyoji",
/// a character name), so query keywords found verbatim in a description earn a boost.
/// Text is folded so onmyoji, onmyouji and onmyōji all meet.
/// </summary>
public static partial class DescriptionKeywords
{
    private static readonly HashSet<string> StopWords = new(new[]
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "at", "by", "for", "from", "with", "without", "about", "into", "over",
        "is", "are", "was", "were", "be", "been", "being", "has", "have", "had", "do", "does", "did", "will", "would", "can", "could",
        "that", "this", "these", "those", "who", "whom", "whose", "which", "where", "when", "what", "how", "why",
        "it", "its", "he", "she", "they", "them", "his", "her", "their", "him", "as", "but", "so", "than", "then", "very", "some", "any",
        "story", "stories", "about", "like", "set", "one", "two", "gets", "get", "goes", "go", "way", "thing", "things",
        "keep", "keeps", "kept", "every", "each", "same", "after", "before", "again", "just", "only", "also", "not", "all",
        "won't", "don't", "doesn't", "isn't", "can't", "there", "here", "still", "ever", "never", "always", "while", "during",
    }.Select(Fold), StringComparer.Ordinal);

    public static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            sb.Append(char.ToLowerInvariant(c));
        }

        return DoubledVowel().Replace(sb.ToString().Replace("ou", "o"), "$1");
    }

    /// <summary>Content words worth matching literally; Japanese yields kanji runs and katakana words.</summary>
    public static List<string> Extract(string query)
    {
        var folded = Fold(query);
        var keywords = new List<string>();
        foreach (Match m in LatinWord().Matches(folded))
        {
            var w = m.Value;
            if (w.Length >= 3 && !StopWords.Contains(w))
                keywords.Add(w);
        }

        foreach (Match m in KanjiRun().Matches(folded))
            keywords.Add(m.Value);
        foreach (Match m in KatakanaRun().Matches(folded))
            if (m.Value.Length >= 3)
                keywords.Add(m.Value);

        return keywords.Distinct().ToList();
    }

    /// <summary>Inverse document frequency per keyword over the folded descriptions; words in no description weigh as if in one.</summary>
    public static Dictionary<string, float> RarityWeights(IReadOnlyList<string> keywords, IEnumerable<string> foldedDescriptions)
    {
        var weights = new Dictionary<string, float>(keywords.Count, StringComparer.Ordinal);
        if (keywords.Count == 0)
            return weights;
        var counts = new int[keywords.Count];
        var total = 0;
        foreach (var text in foldedDescriptions)
        {
            total++;
            for (var i = 0; i < keywords.Count; i++)
                if (ContainsKeyword(text, keywords[i]))
                    counts[i]++;
        }

        for (var i = 0; i < keywords.Count; i++)
            weights[keywords[i]] = MathF.Log((total + 1f) / (Math.Max(counts[i], 1) + 1f)) + 0.1f;
        return weights;
    }

    /// <summary>Share of the query's rarity weight covered by the hits, 0..1.</summary>
    public static float WeightedCoverage(IReadOnlyList<string> hits, Dictionary<string, float> weights)
    {
        if (weights.Count == 0)
            return 0;
        var total = weights.Values.Sum();
        if (total <= 0)
            return 0;
        var got = 0f;
        foreach (var h in hits)
            if (weights.TryGetValue(h, out var w))
                got += w;
        return got / total;
    }

    /// <summary>Keywords present verbatim in the folded description.</summary>
    public static string[] Hits(IReadOnlyList<string> keywords, string foldedDescription)
    {
        if (keywords.Count == 0)
            return [];
        var hits = new List<string>();
        foreach (var k in keywords)
            if (ContainsKeyword(foldedDescription, k))
                hits.Add(k);
        return hits.ToArray();
    }

    private static bool ContainsKeyword(string foldedText, string keyword)
    {
        if (keyword[0] >= 128)
            return foldedText.Contains(keyword, StringComparison.Ordinal);

        var from = 0;
        while (true)
        {
            var idx = foldedText.IndexOf(keyword, from, StringComparison.Ordinal);
            if (idx < 0)
                return false;
            var end = idx + keyword.Length;
            if ((idx == 0 || !IsWordChar(foldedText[idx - 1])) && (end == foldedText.Length || !IsWordChar(foldedText[end])))
                return true;
            from = idx + 1;
        }
    }

    private static bool IsWordChar(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';

    [GeneratedRegex(@"([aeiou])\1")]
    private static partial Regex DoubledVowel();

    [GeneratedRegex(@"[a-z][a-z'\-]*")]
    private static partial Regex LatinWord();

    [GeneratedRegex(@"[一-鿿]{2,}")]
    private static partial Regex KanjiRun();

    [GeneratedRegex(@"[゠-ヿ]+")]
    private static partial Regex KatakanaRun();
}
