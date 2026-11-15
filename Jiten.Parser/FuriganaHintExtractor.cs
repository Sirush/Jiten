using System.Text;
using System.Text.RegularExpressions;

namespace Jiten.Parser;

public readonly record struct FuriganaHint(int Offset, int Length, string Reading);

public static partial class FuriganaHintExtractor
{
    public const string AnnotationPattern = @"\{([^\u0001-\u007F]{1,32})'([^'{}\n]{1,32})\}";

    [GeneratedRegex(AnnotationPattern)]
    private static partial Regex HintPattern();

    /// <summary>Drops inline furigana markup ({base'reading}), keeping only the base text.</summary>
    public static string Strip(string annotatedText) =>
        string.IsNullOrEmpty(annotatedText) ? annotatedText : HintPattern().Replace(annotatedText, "$1");

    public static (string CleanText, FuriganaHint[] Hints) Extract(string annotatedText)
    {
        var matches = HintPattern().Matches(annotatedText);
        if (matches.Count == 0)
            return (annotatedText, []);

        var hints = new List<FuriganaHint>(matches.Count);
        var sb = new StringBuilder(annotatedText.Length);
        int lastEnd = 0;

        foreach (Match match in matches)
        {
            sb.Append(annotatedText, lastEnd, match.Index - lastEnd);

            var baseText = match.Groups[1].Value;
            var reading = match.Groups[2].Value;

            if (baseText.Length > 0 && reading.Length > 0)
                hints.Add(new FuriganaHint(sb.Length, baseText.Length, reading));

            sb.Append(baseText);
            lastEnd = match.Index + match.Length;
        }

        sb.Append(annotatedText, lastEnd, annotatedText.Length - lastEnd);
        return (sb.ToString(), hints.ToArray());
    }

    public static string Annotate(string cleanText, ReadOnlySpan<FuriganaHint> hints)
    {
        if (hints.Length == 0) return cleanText;

        var sb = new StringBuilder(cleanText.Length + hints.Length * 6);
        int lastEnd = 0;

        foreach (var hint in hints)
        {
            sb.Append(cleanText, lastEnd, hint.Offset - lastEnd);
            sb.Append('{');
            sb.Append(cleanText, hint.Offset, hint.Length);
            sb.Append('\'');
            sb.Append(hint.Reading);
            sb.Append('}');
            lastEnd = hint.Offset + hint.Length;
        }

        sb.Append(cleanText, lastEnd, cleanText.Length - lastEnd);
        return sb.ToString();
    }

    /// <summary>Reading of the clean text with every hinted base replaced by its reading; unhinted characters are kept.</summary>
    public static string ApplyReadings(string cleanText, ReadOnlySpan<FuriganaHint> hints)
    {
        if (hints.Length == 0) return cleanText;

        var sb = new StringBuilder(cleanText.Length * 2);
        int lastEnd = 0;

        foreach (var hint in hints)
        {
            sb.Append(cleanText, lastEnd, hint.Offset - lastEnd);
            sb.Append(hint.Reading);
            lastEnd = hint.Offset + hint.Length;
        }

        sb.Append(cleanText, lastEnd, cleanText.Length - lastEnd);
        return sb.ToString();
    }

    private const int ResyncWindow = 32;
    private const int ResyncMatchLength = 4;

    /// <summary>Maps hint offsets from the clean text onto its preprocessed copy by walking both in step, so a base lands on its own occurrence and not on an earlier identical character.</summary>
    internal static FuriganaHint[] RelocateToCleanedOriginal(
        string cleanedOriginal, FuriganaHint[] hints, string cleanText)
    {
        if (hints.Length == 0) return [];

        var relocated = new List<FuriganaHint>(hints.Length);
        int cleanPos = 0, originalPos = 0;

        foreach (var hint in hints)
        {
            if (hint.Offset < cleanPos) continue;

            while (cleanPos < hint.Offset && originalPos < cleanedOriginal.Length)
            {
                if (cleanText[cleanPos] == cleanedOriginal[originalPos])
                {
                    cleanPos++;
                    originalPos++;
                }
                else if (!TryResync(cleanText, cleanedOriginal, ref cleanPos, ref originalPos, hint.Offset))
                {
                    cleanPos++;
                }
            }

            if (cleanPos < hint.Offset) continue;

            int found = MatchesAt(cleanText, cleanPos, cleanedOriginal, originalPos, hint.Length) ? originalPos : -1;
            for (int skip = 1; skip <= ResyncWindow && found < 0; skip++)
                if (MatchesAt(cleanText, cleanPos, cleanedOriginal, originalPos + skip, hint.Length + ResyncMatchLength))
                    found = originalPos + skip;
            if (found < 0) continue;

            relocated.Add(new FuriganaHint(found, hint.Length, hint.Reading));
            cleanPos = hint.Offset + hint.Length;
            originalPos = found + hint.Length;
        }

        return relocated.ToArray();
    }

    private static bool TryResync(string cleanText, string cleanedOriginal, ref int cleanPos, ref int originalPos, int cleanLimit)
    {
        for (int skipped = 1; skipped <= 2 * ResyncWindow; skipped++)
        {
            for (int originalSkip = Math.Max(0, skipped - ResyncWindow); originalSkip <= Math.Min(skipped, ResyncWindow); originalSkip++)
            {
                int c = cleanPos + skipped - originalSkip;
                int o = originalPos + originalSkip;
                if (c > cleanLimit || !MatchesAt(cleanText, c, cleanedOriginal, o, ResyncMatchLength)) continue;

                cleanPos = c;
                originalPos = o;
                return true;
            }
        }

        return false;
    }

    private static bool MatchesAt(string cleanText, int cleanPos, string cleanedOriginal, int originalPos, int maxLength)
    {
        int length = Math.Min(maxLength, cleanText.Length - cleanPos);
        if (length <= 0 || originalPos + length > cleanedOriginal.Length) return false;

        return cleanText.AsSpan(cleanPos, length).SequenceEqual(cleanedOriginal.AsSpan(originalPos, length));
    }
}
