namespace Jiten.Api.Helpers;

/// <summary>
/// Helpers for mapping parsed tokens back onto source text, tolerating
/// expressive ー characters that the parser's vowel elongation repair strips
/// (e.g., source "なーい" → parsed token "ない"), line breaks the parser merges
/// tokens across (source "ことに\nなりまして" → token "ことになりまして"), and
/// colloquial preprocessing transformations (e.g., とんでもねえ → とんでもない).
/// </summary>
public static class TokenPositionHelper
{
    /// <summary>
    /// Finds a token in the source text. Falls back to a fuzzy match that skips ー and
    /// line breaks in the source when the exact match fails, then tries reversing known
    /// preprocessing transformations.
    /// Returns (position, sourceLength) where sourceLength accounts for any skipped
    /// character or reversed preprocessing.
    /// </summary>
    public static (int position, int sourceLength) FindTokenInSource(string source, string token, int startFrom)
    {
        int pos = source.IndexOf(token, startFrom, StringComparison.Ordinal);
        if (pos >= 0) return (pos, token.Length);

        var fuzzy = FuzzyMatchSkippingUnmatchable(source, token, startFrom);
        if (fuzzy.position >= 0) return fuzzy;

        string reversed = ReversePreprocessing(token);
        if (reversed != token)
        {
            pos = source.IndexOf(reversed, startFrom, StringComparison.Ordinal);
            if (pos >= 0) return (pos, reversed.Length);

            fuzzy = FuzzyMatchSkippingUnmatchable(source, reversed, startFrom);
            if (fuzzy.position >= 0) return fuzzy;
        }

        return (-1, 0);
    }

    /// <summary>
    /// Skips characters the parser drops from token text: ー/… from vowel elongation repair,
    /// and line breaks, which the analyser tokenises across (so a merged token's text is
    /// contiguous while its source span is not).
    /// </summary>
    private static (int position, int sourceLength) FuzzyMatchSkippingUnmatchable(string source, string token, int startFrom)
    {
        for (int i = startFrom; i <= source.Length - token.Length; i++)
        {
            // Anchor on the token's first character so a skipped leading character is never
            // folded into the returned span, which would push it outside its own paragraph.
            if (token.Length > 0 && source[i] != token[0]) continue;

            int si = i, ti = 0;
            while (si < source.Length && ti < token.Length)
            {
                if (source[si] == token[ti]) { si++; ti++; }
                else if (source[si] is 'ー' or '…' or '\n' or '\r') { si++; }
                else break;
            }

            if (ti == token.Length) return (i, si - i);
        }

        return (-1, 0);
    }

    /// <summary>
    /// Reverses the content-changing substitutions applied by MorphologicalAnalyser.PreprocessText
    /// so that preprocessed token text can be matched against raw source text.
    /// </summary>
    private static string ReversePreprocessing(string token)
    {
        var result = token
            .Replace("とんでもない", "とんでもねえ")
            .Replace("しょうがない", "しょうがねえ")
            .Replace("にちがいない", "にちがいねえ")
            .Replace("来い", "来イ")
            .Replace('頸', '頚');
        if (result.EndsWith("さい"))
            result = string.Concat(result.AsSpan(0, result.Length - 2), "せぇ");
        return result;
    }
}
