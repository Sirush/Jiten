using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SubtitlesParser.Classes.Parsers;

namespace Jiten.Core;

public partial class SubtitleExtractor
{
    public static readonly string[] SupportedExtensions = [".ass", ".srt", ".ssa"];
    // ASS styles/markers to skip (CN lines)
    public static readonly string[] ChineseLineMarkers =
    [
        "cn",
        "720通用注釋",
        "1080通用注釋",
        "通用720中文",
        "單語720中文",
        "通用1080中文",
        "單語1080中文",
        "花語製作通用",
        "CN",
        "CN2",
        "Default-CN",
        "OPCN",
        "EDCN",
        "EDJP",
        "ED",
        "SCR",
        "STAFF",
        "CHI",
        "NAME",
        "EDSC"
    ];
    
    [GeneratedRegex(@"\((.*?)\)")]
    private static partial Regex RubyPattern();

    [GeneratedRegex(@"（(.*?)）")]
    private static partial Regex FullWidthRubyPattern();

    [GeneratedRegex(@"\[.*?\]")]
    private static partial Regex SquareBracketPattern();
    
    [GeneratedRegex(@"\{.*?}]")]
    private static partial Regex CurlyBracesPattern();

    /// <summary>
    /// Extract plain text from a subtitle file (.ass, .srt, .ssa)
    /// </summary>
    public async Task<string> Extract(string filePath)
    {
        var lines = await ExtractCueLines(filePath);
        return string.Join(Environment.NewLine, lines.Select(l => l.Text));
    }

    /// <summary>The lines <see cref="Extract"/> joins, flagged when a line is the last one kept from its subtitle cue.</summary>
    public async Task<List<CueLine>> ExtractCueLines(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        // Preprocess ASS files (remove comments, cn lines)
        if (extension is ".ass" or ".ssa")
        {
            filePath = await PreprocessAssFile(filePath);
        }

        var parser = new SubParser();
        await using var fileStream = File.OpenRead(filePath);
        var items = parser.ParseStream(fileStream, Encoding.UTF8);

        var lines = new List<CueLine>();
        foreach (var item in items)
        {
            int cueStart = lines.Count;
            foreach (var rawLine in item.PlaintextLines)
            {
                // Remove ruby/furigana annotations in both half-width and full-width parentheses
                var line = RubyPattern().Replace(rawLine, "");
                line = FullWidthRubyPattern().Replace(line, "");
                line = SquareBracketPattern().Replace(line, "");
                line = CurlyBracesPattern().Replace(line, "");

                if (!string.IsNullOrWhiteSpace(line))
                    lines.Add(new CueLine(line, false));
            }

            if (lines.Count > cueStart)
                lines[^1] = lines[^1] with { EndsCue = true };
        }

        return lines;
    }

    /// <summary>
    /// Extract subtitle items with timing (milliseconds) and raw text.
    /// </summary>
    public async Task<List<SubtitleItem>> ExtractItems(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        if (extension is ".ass" or ".ssa")
        {
            filePath = await PreprocessAssFile(filePath);
            extension = Path.GetExtension(filePath).ToLowerInvariant();
        }

        return extension switch
        {
            ".srt" => await ParseSrtItems(filePath),
            ".ass" or ".ssa" => await ParseAssItems(filePath),
            _ => []
        } is { Count: > 0 } rawItems
            ? MergeDuplicateItems(CleanItemText(rawItems), maxGapMs: 3000, minLengthForGap: 8)
            : [];
    }

    /// <summary>
    /// Preprocess ASS file by removing comments and chinese lines, converting to SSA
    /// </summary>
    private async Task<string> PreprocessAssFile(string filePath)
    {
        var ssaPath = Path.ChangeExtension(filePath, ".ssa");
        var lines = await File.ReadAllLinesAsync(filePath);
        var filteredLines = lines
            .Where(line =>
            {
                if (line.TrimStart().StartsWith(';'))
                    return false;
                if (line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
                    return false;

                var styleName = GetAssStyleName(line);
                if (styleName != null && ChineseLineMarkers.Any(marker =>
                        styleName.Equals(marker, StringComparison.OrdinalIgnoreCase)))
                    return false;

                return true;
            })
            .ToList();
        await File.WriteAllLinesAsync(ssaPath, filteredLines);
        return ssaPath;
    }

    private static string? GetAssStyleName(string line)
    {
        if (line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
        {
            var afterColon = line.AsSpan(6).TrimStart();
            var commaIdx = afterColon.IndexOf(',');
            return commaIdx >= 0 ? afterColon[..commaIdx].Trim().ToString() : afterColon.Trim().ToString();
        }

        if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
        {
            var afterColon = line.AsSpan(9).TrimStart();
            int commaCount = 0;
            for (int i = 0; i < afterColon.Length; i++)
            {
                if (afterColon[i] == ',')
                {
                    commaCount++;
                    if (commaCount == 3)
                    {
                        var rest = afterColon[(i + 1)..];
                        var nextComma = rest.IndexOf(',');
                        return nextComma >= 0 ? rest[..nextComma].Trim().ToString() : rest.Trim().ToString();
                    }
                }
            }
        }

        return null;
    }

    private static async Task<List<SubtitleItem>> ParseSrtItems(string filePath)
    {
        var lines = await File.ReadAllLinesAsync(filePath);
        var items = new List<SubtitleItem>();

        int i = 0;
        int track = 0;
        int prevSeqNum = 0;
        while (i < lines.Length)
        {
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i]))
                i++;

            if (i >= lines.Length)
                break;

            var line = lines[i].Trim();

            // Optional numeric index line — detect track boundary when sequence resets
            if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seqNum))
            {
                if (seqNum <= prevSeqNum && prevSeqNum > 1)
                    track++;
                prevSeqNum = seqNum;

                i++;
                if (i >= lines.Length)
                    break;
            }

            if (i >= lines.Length)
                break;

            if (!TryParseSrtTimeRange(lines[i], out var startMs, out var endMs))
            {
                i++;
                continue;
            }

            i++;
            var textLines = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
            {
                textLines.Add(lines[i]);
                i++;
            }

            var text = string.Join("\n", textLines);
            items.Add(new SubtitleItem(startMs, endMs, text, track));
        }

        return items;
    }

    private static List<SubtitleItem> CleanItemText(List<SubtitleItem> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var cleaned = SubtitleTextCleaner.CleanText(item.Text);
            if (cleaned == item.Text)
                continue;

            items[i] = new SubtitleItem(item.StartMs, item.EndMs, cleaned, item.TrackIndex);
        }

        return items;
    }

    private static List<SubtitleItem> MergeDuplicateItems(IEnumerable<SubtitleItem> items, int maxGapMs = 0, int minLengthForGap = 0)
    {
        var grouped = new Dictionary<(string text, int track), List<(int start, int end)>>();
        foreach (var item in items)
        {
            var key = (item.Text, item.TrackIndex);
            if (!grouped.TryGetValue(key, out var spans))
            {
                spans = [];
                grouped[key] = spans;
            }

            spans.Add((item.StartMs, item.EndMs));
        }

        var mergedItems = new List<SubtitleItem>();
        foreach (var ((text, track), spans) in grouped)
        {
            spans.Sort((a, b) =>
            {
                var startCompare = a.start.CompareTo(b.start);
                return startCompare != 0 ? startCompare : a.end.CompareTo(b.end);
            });

            if (spans.Count == 1)
            {
                mergedItems.Add(new SubtitleItem(spans[0].start, spans[0].end, text, track));
                continue;
            }

            var gapLimit = GetTextLength(text) >= minLengthForGap ? maxGapMs : 0;
            var currentStart = spans[0].start;
            var currentEnd = spans[0].end;

            for (int i = 1; i < spans.Count; i++)
            {
                var (start, end) = spans[i];
                if (start <= currentEnd + gapLimit)
                {
                    currentEnd = Math.Max(currentEnd, end);
                }
                else
                {
                    mergedItems.Add(new SubtitleItem(currentStart, currentEnd, text, track));
                    currentStart = start;
                    currentEnd = end;
                }
            }

            mergedItems.Add(new SubtitleItem(currentStart, currentEnd, text, track));
        }

        mergedItems.Sort((a, b) =>
        {
            var startCompare = a.StartMs.CompareTo(b.StartMs);
            if (startCompare != 0)
                return startCompare;
            var endCompare = a.EndMs.CompareTo(b.EndMs);
            if (endCompare != 0)
                return endCompare;
            return string.Compare(a.Text, b.Text, StringComparison.Ordinal);
        });

        return mergedItems;
    }

    private static int GetTextLength(string text)
    {
        var stripped = SubtitleTextCleaner.StripNonSpoken(text);
        if (string.IsNullOrEmpty(stripped))
            return 0;

        return stripped.Replace("\n", "", StringComparison.Ordinal).Length;
    }

    private static bool TryParseSrtTimeRange(string line, out int startMs, out int endMs)
    {
        startMs = 0;
        endMs = 0;

        var parts = line.Split("-->", StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        var startText = ExtractSrtTimestamp(parts[0]);
        var endText = ExtractSrtTimestamp(parts[1]);
        if (startText == null || endText == null)
            return false;

        startMs = ParseSrtTimestamp(startText);
        endMs = ParseSrtTimestamp(endText);
        return true;
    }

    private static string? ExtractSrtTimestamp(string text)
    {
        var match = Regex.Match(text, @"\d{1,2}:\d{2}:\d{2}[,\.]\d{1,3}");
        return match.Success ? match.Value : null;
    }

    private static int ParseSrtTimestamp(string timestamp)
    {
        var normalized = timestamp.Replace('.', ',');
        var parts = normalized.Split([':', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
            return 0;

        var hours = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minutes = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var seconds = int.Parse(parts[2], CultureInfo.InvariantCulture);
        var milliseconds = int.Parse(parts[3], CultureInfo.InvariantCulture);
        if (parts[3].Length == 1)
            milliseconds *= 100;
        else if (parts[3].Length == 2)
            milliseconds *= 10;

        return (int)((hours * 3600 + minutes * 60 + seconds) * 1000L + milliseconds);
    }

    private static async Task<List<SubtitleItem>> ParseAssItems(string filePath)
    {
        var items = new List<SubtitleItem>();
        bool inEvents = false;
        List<string>? format = null;
        int idxStart = -1;
        int idxEnd = -1;
        int idxText = -1;

        var lines = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[Events]", StringComparison.OrdinalIgnoreCase))
            {
                inEvents = true;
                format = null;
                idxStart = idxEnd = idxText = -1;
                continue;
            }

            if (trimmed.StartsWith("[") && !trimmed.StartsWith("[Events]", StringComparison.OrdinalIgnoreCase))
            {
                inEvents = false;
                continue;
            }

            if (!inEvents)
                continue;

            if (trimmed.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var colonIndex = line.IndexOf(':');
                var rest = colonIndex >= 0 ? line[(colonIndex + 1)..] : string.Empty;
                format = rest.Split(',')
                             .Select(f => f.Trim())
                             .ToList();

                idxStart = format.FindIndex(f => f.Equals("Start", StringComparison.OrdinalIgnoreCase));
                idxEnd = format.FindIndex(f => f.Equals("End", StringComparison.OrdinalIgnoreCase));
                idxText = format.FindIndex(f => f.Equals("Text", StringComparison.OrdinalIgnoreCase));
                continue;
            }

            if (!trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (format == null || idxStart < 0 || idxEnd < 0 || idxText < 0)
                continue;

            var colon = line.IndexOf(':');
            if (colon < 0)
                continue;

            var restLine = line[(colon + 1)..].TrimStart();
            var fields = restLine.Split(',', format.Count);
            if (fields.Length <= Math.Max(idxText, Math.Max(idxStart, idxEnd)))
                continue;

            var start = ParseAssTime(fields[idxStart]);
            var end = ParseAssTime(fields[idxEnd]);
            var text = fields[idxText];

            items.Add(new SubtitleItem(start, end, text));
        }

        return items;
    }

    private static int ParseAssTime(string timestamp)
    {
        // ASS format: H:MM:SS.CS (centiseconds)
        var parts = timestamp.Trim().Split(':');
        if (parts.Length != 3)
            return 0;

        var hours = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minutes = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var secondsParts = parts[2].Split('.');
        var seconds = int.Parse(secondsParts[0], CultureInfo.InvariantCulture);
        var centiseconds = secondsParts.Length > 1 ? int.Parse(secondsParts[1], CultureInfo.InvariantCulture) : 0;

        return (int)((hours * 3600 + minutes * 60 + seconds) * 1000L + centiseconds * 10L);
    }
}

public readonly record struct SubtitleItem(int StartMs, int EndMs, string Text, int TrackIndex = 0);

public readonly record struct CueLine(string Text, bool EndsCue);
