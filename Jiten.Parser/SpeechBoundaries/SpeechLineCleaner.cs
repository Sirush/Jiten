using System.Text;
using System.Text.RegularExpressions;
using Jiten.Core;

namespace Jiten.Parser.SpeechBoundaries;

/// <summary>Per-line cleanup for subtitle text. <see cref="Clean"/> keeps the markers the boundary model reads; <see cref="ToSentenceText"/> removes them for display.</summary>
public static partial class SpeechLineCleaner
{
    [GeneratedRegex(@"[｡-ﾟ]+")]
    private static partial Regex HalfwidthKatakanaRun();

    [GeneratedRegex(@"[♪♫♬♩≪≫＜＞<>《》〈〉]")]
    private static partial Regex DisplayMarks();

    [GeneratedRegex(@"^[-－‐―–—]+\s*")]
    private static partial Regex LeadingSpeakerDash();

    [GeneratedRegex(@"[➡→―—\s　]+$")]
    private static partial Regex TrailingContinuationMarks();

    [GeneratedRegex(@"[ \t　]+")]
    private static partial Regex SpaceRun();

    [GeneratedRegex(@"[A-Za-z0-9Ａ-Ｚａ-ｚ０-９]")]
    private static partial Regex LatinWordChar();

    /// <returns>The normalised line, or an empty string when nothing spoken is left.</returns>
    public static string Clean(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0)
            return line;

        // Halfwidth katakana and ｡｢｣､ are invisible to the parser until folded to their fullwidth forms.
        line = HalfwidthKatakanaRun().Replace(line, m => m.Value.Normalize(NormalizationForm.FormKC));
        line = line.Replace('!', '！').Replace('?', '？');

        return SubtitleTextCleaner.StripNonSpoken(line);
    }

    public static string ToSentenceText(string cleanedLine)
    {
        var text = DisplayMarks().Replace(cleanedLine, "");
        text = LeadingSpeakerDash().Replace(text.Trim(), "");
        text = TrailingContinuationMarks().Replace(text, "").Trim();

        return SpaceRun().Replace(text, m => PauseReplacement(text, m));
    }

    private static string PauseReplacement(string text, Match space)
    {
        var end = space.Index + space.Length;
        if (space.Index == 0 || end == text.Length)
            return "";

        var before = text[space.Index - 1];
        var after = text[end];
        var latinBefore = IsLatinWordChar(before);
        var latinAfter = IsLatinWordChar(after);
        if (latinBefore && latinAfter)
            return " ";

        if (latinBefore || latinAfter || char.IsPunctuation(before) || char.IsPunctuation(after))
            return "";

        return "、";
    }

    private static bool IsLatinWordChar(char c) => LatinWordChar().IsMatch(c.ToString());
}
