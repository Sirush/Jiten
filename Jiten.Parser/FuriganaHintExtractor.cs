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

    internal static FuriganaHint[] RelocateToCleanedOriginal(
        string cleanedOriginal, FuriganaHint[] hints, string cleanText)
    {
        if (hints.Length == 0) return [];

        var relocated = new List<FuriganaHint>(hints.Length);
        int searchStart = 0;

        foreach (var hint in hints)
        {
            var span = cleanText.AsSpan(hint.Offset, hint.Length);
            int found = cleanedOriginal.AsSpan(searchStart).IndexOf(span, StringComparison.Ordinal);
            if (found >= 0)
            {
                found += searchStart;
            }
            if (found >= 0)
            {
                relocated.Add(new FuriganaHint(found, hint.Length, hint.Reading));
                searchStart = found + hint.Length;
            }
        }

        return relocated.ToArray();
    }
}
