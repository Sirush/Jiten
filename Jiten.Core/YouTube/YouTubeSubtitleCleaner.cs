using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jiten.Core.YouTube;

public record YouTubeSubtitleCue(int StartMs, int EndMs, List<string> Lines);

public class YouTubeSubtitleCleanResult
{
    public List<YouTubeSubtitleCue> Cues { get; } = new();
    public int DroppedReadingLines { get; set; }
    public int DroppedLatinLines { get; set; }
    public int DroppedEmptyCues { get; set; }

    /// <summary>Non-whitespace characters left after cleaning</summary>
    public int CharacterCount => Cues.Sum(c => c.Lines.Sum(l => l.Count(ch => !char.IsWhiteSpace(ch))));

    public int LatinLineShare(int totalLinesBefore) =>
        totalLinesBefore == 0 ? 0 : DroppedLatinLines * 100 / totalLinesBefore;
}

/// <summary>
/// Normalises a manual YouTube track (srt or vtt) into a plain srt the standard subtitle pipeline can read.
/// YouTube-specific hazards: a kana reading line duplicated under each kanji line, and translation lines
/// mixed into a nominally Japanese track.
/// </summary>
public static partial class YouTubeSubtitleCleaner
{
    [GeneratedRegex(@"<[^>]*>|\{[^}]*\}")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"(\d{1,2}:)?\d{1,2}:\d{2}[.,]\d{1,3}")]
    private static partial Regex TimestampPattern();

    [GeneratedRegex(@"[\p{IsHiragana}\p{IsKatakana}ー]")]
    private static partial Regex KanaPattern();

    [GeneratedRegex(@"[\p{IsCJKUnifiedIdeographs}々〆〇]")]
    private static partial Regex KanjiPattern();

    [GeneratedRegex(@"[A-Za-z]")]
    private static partial Regex LatinPattern();

    public static async Task<YouTubeSubtitleCleanResult> CleanFileAsync(string inputPath, string outputSrtPath)
    {
        var text = await File.ReadAllTextAsync(inputPath, Encoding.UTF8);
        var result = Clean(text);
        await File.WriteAllTextAsync(outputSrtPath, ToSrt(result.Cues), new UTF8Encoding(false));
        return result;
    }

    public static YouTubeSubtitleCleanResult Clean(string subtitleText)
    {
        var result = new YouTubeSubtitleCleanResult();

        foreach (var cue in ParseCues(subtitleText))
        {
            var lines = cue.Lines.Select(l => TagPattern().Replace(l, "").Trim())
                           .Where(l => l.Length > 0)
                           .ToList();

            var withoutLatin = lines.Where(l => !IsTranslationLine(l)).ToList();
            result.DroppedLatinLines += lines.Count - withoutLatin.Count;

            var withoutReadings = DropReadingLines(withoutLatin, out var droppedReadings);
            result.DroppedReadingLines += droppedReadings;

            if (withoutReadings.Count == 0)
            {
                result.DroppedEmptyCues++;
                continue;
            }

            result.Cues.Add(new YouTubeSubtitleCue(cue.StartMs, cue.EndMs, withoutReadings));
        }

        return result;
    }

    public static string ToSrt(IEnumerable<YouTubeSubtitleCue> cues)
    {
        var builder = new StringBuilder();
        var index = 1;
        foreach (var cue in cues)
        {
            builder.Append(index++).Append('\n');
            builder.Append(FormatSrtTime(cue.StartMs)).Append(" --> ").Append(FormatSrtTime(cue.EndMs)).Append('\n');
            builder.Append(string.Join('\n', cue.Lines)).Append("\n\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Reads srt and vtt alike: any "start --> end" line opens a cue, everything until a blank line is text.
    /// </summary>
    public static List<YouTubeSubtitleCue> ParseCues(string text)
    {
        var cues = new List<YouTubeSubtitleCue>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].Trim();
            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                i++;
                continue;
            }

            var startMatch = TimestampPattern().Match(line[..arrow]);
            var endMatch = TimestampPattern().Match(line[(arrow + 3)..]);
            i++;
            if (!startMatch.Success || !endMatch.Success)
                continue;

            var cueLines = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                cueLines.Add(lines[i]);
                i++;
            }

            cues.Add(new YouTubeSubtitleCue(ParseTimestamp(startMatch.Value), ParseTimestamp(endMatch.Value), cueLines));
        }

        return cues;
    }

    /// <summary>
    /// A line with Latin letters and no Japanese script is a translation, not speech.
    /// </summary>
    public static bool IsTranslationLine(string line)
    {
        if (KanaPattern().IsMatch(line) || KanjiPattern().IsMatch(line))
            return false;
        return LatinPattern().Matches(line).Count >= 2;
    }

    /// <summary>
    /// Drops kana-only lines that are a reading of another line in the same cue: the kanji line's own kana
    /// must appear in order inside the candidate, and the candidate must be at least as long.
    /// </summary>
    private static List<string> DropReadingLines(List<string> lines, out int dropped)
    {
        dropped = 0;
        if (lines.Count < 2)
            return lines;

        var kept = new List<string>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var candidate = lines[i];
            var isReading = !KanjiPattern().IsMatch(candidate) &&
                            KanaPattern().IsMatch(candidate) &&
                            lines.Where((_, j) => j != i).Any(other => IsReadingOf(candidate, other));
            if (isReading)
            {
                dropped++;
                continue;
            }

            kept.Add(candidate);
        }

        return kept;
    }

    private static bool IsReadingOf(string candidate, string kanjiLine)
    {
        if (!KanjiPattern().IsMatch(kanjiLine))
            return false;

        var candidateCompact = Compact(candidate);
        var kanjiCompact = Compact(kanjiLine);
        if (candidateCompact.Length < kanjiCompact.Length)
            return false;

        var kanaOfKanjiLine = kanjiCompact.Where(c => KanaPattern().IsMatch(c.ToString())).ToList();
        if (kanaOfKanjiLine.Count == 0)
            return candidateCompact.Length <= kanjiCompact.Length * 4;

        var position = 0;
        foreach (var kana in kanaOfKanjiLine)
        {
            position = candidateCompact.IndexOf(kana, position);
            if (position < 0)
                return false;
            position++;
        }

        return true;
    }

    private static string Compact(string line)
    {
        var builder = new StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || c is '　' or '、' or '。' or '・' or '…')
                continue;
            builder.Append(c);
        }

        return builder.ToString();
    }

    private static int ParseTimestamp(string value)
    {
        var parts = value.Replace(',', '.').Split(':');
        var seconds = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        var minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        var hours = parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        return (int)Math.Round((hours * 3600 + minutes * 60 + seconds) * 1000);
    }

    private static string FormatSrtTime(int ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
    }
}
