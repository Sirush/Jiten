using System.Collections;
using System.Text.RegularExpressions;

namespace Jiten.Parser.SpeechBoundaries;

/// <summary>YouTube sentence boundaries: a video's own punctuation where it proves reliable, plus the YouTube model trained on punctuated videos.</summary>
public static partial class YouTubeSpeechText
{
    /// <summary>Below this many polite-ending lines a video says too little about its punctuation habits to be trusted.</summary>
    public const int MinPoliteLines = 10;

    /// <summary>Share of polite-ending lines that must carry terminal punctuation; writers who punctuate score 99-100%.</summary>
    public const double MinPunctuatedPoliteShare = 0.9;

    /// <summary>A prefix seen this often at line starts in one video is a speaker label, not speech.</summary>
    private const int MinSpeakerLabelCount = 3;

    [GeneratedRegex(@"(です|ます|でした|ました|ません|でしょう)[ねよか]{0,2}[。！？…]?$")]
    private static partial Regex PoliteEnding();

    [GeneratedRegex(@"[」』）)♪〜～ｗwＷW\s　]+$")]
    private static partial Regex TrailingClosers();

    [GeneratedRegex(@"^(?<name>[^\s　（(「『【\[<＜：:／/）)、。！？!?…]{1,8}?)\s*[）)：:／/]\s*(?=[^\s　/])")]
    private static partial Regex SpeakerPrefix();

    [GeneratedRegex(@"^[0-9０-９\p{P}]+$")]
    private static partial Regex NotAName();

    [GeneratedRegex(@"^(?:(?:うん|うーん|はい|ええ|ああ|あー|そう|へえ|へー|ほう|おお|なるほど|確かに)[、，\s　]*)+[。！？…ー〜～]*$")]
    private static partial Regex BackchannelOnly();

    [GeneratedRegex(@"^[ｗwＷW笑草\s　]+$")]
    private static partial Regex LaughOnly();

    [GeneratedRegex(@"[。！？、，]+(?=[」』）)\s　]*$)")]
    private static partial Regex LineFinalPunctuation();

    [GeneratedRegex(@"[。！？、，]+")]
    private static partial Regex InnerPunctuation();

    [GeneratedRegex(@"(?<=[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}ー。！？!?、…）)」』])[ｗwＷW]{2,}$")]
    private static partial Regex TrailingLaugh();

    /// <summary>Drops speaker labels and laugh-only lines; the line count never changes, so a stored bitmap still lines up with the raw text.</summary>
    public static string Normalise(string rawText)
    {
        var lines = rawText.Split('\n');
        var speakers = SpeakerLabels(lines);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (speakers.Count > 0)
            {
                var match = SpeakerPrefix().Match(line.TrimStart());
                if (match.Success && speakers.Contains(match.Groups["name"].Value))
                    line = line.TrimStart()[match.Length..];
            }

            if (LaughOnly().IsMatch(line.Trim()))
                line = "";
            else
                line = TrailingLaugh().Replace(line.TrimEnd('\r', ' ', '　'), "");

            lines[i] = line;
        }

        return string.Join('\n', lines);
    }

    private static HashSet<string> SpeakerLabels(string[] lines)
    {
        var counts = new Dictionary<string, int>();
        foreach (var line in lines)
        {
            var match = SpeakerPrefix().Match(line.TrimStart());
            if (!match.Success)
                continue;

            var name = match.Groups["name"].Value;
            if (NotAName().IsMatch(name))
                continue;

            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        return counts.Where(kv => kv.Value >= MinSpeakerLabelCount).Select(kv => kv.Key).ToHashSet();
    }

    /// <param name="cleanedLines">Lines after <see cref="SpeechLineCleaner.Clean"/>.</param>
    public static bool HasTrustedPunctuation(IReadOnlyList<string> cleanedLines)
    {
        int polite = 0, punctuated = 0;
        foreach (var line in cleanedLines)
        {
            if (line.Length == 0 || !PoliteEnding().IsMatch(line))
                continue;

            polite++;
            if (line[^1] is '。' or '！' or '？' or '…')
                punctuated++;
        }

        return polite >= MinPoliteLines && punctuated >= polite * MinPunctuatedPoliteShare;
    }

    /// <summary>A line ends a sentence only when its own punctuation says so, except that a backchannel line is the other speaker's turn and always stands alone.</summary>
    public static BitArray PunctuationBoundaries(IReadOnlyList<string> cleanedLines)
    {
        var bits = new BitArray(cleanedLines.Count);
        int lastKept = -1;
        for (int i = 0; i < cleanedLines.Count; i++)
        {
            if (cleanedLines[i].Length == 0)
                continue;

            if (lastKept >= 0 && BackchannelOnly().IsMatch(cleanedLines[i]))
                bits[lastKept] = true;

            lastKept = i;
            var text = TrailingClosers().Replace(cleanedLines[i], "");
            bits[i] = text.Length > 0 && text[^1] is '。' or '！' or '？' || BackchannelOnly().IsMatch(cleanedLines[i]);
        }

        if (lastKept >= 0)
            bits[lastKept] = true;

        return bits;
    }

    /// <summary>Makes a punctuated line look like an unpunctuated subtitle, which marks pauses with spaces; the boundary model is trained and run on this form.</summary>
    public static string StripPunctuation(string cleanedLine) =>
        InnerPunctuation().Replace(LineFinalPunctuation().Replace(cleanedLine, ""), "　").Trim();

    /// <summary>With trusted punctuation the model only adds cuts it is near certain of: the channel's punctuation misses about one sentence end in six.</summary>
    public const double TrustedModelThreshold = 0.95;

    /// <summary>Best accuracy on the hand-labelled YouTube gold set (86.8%); at 0.5 the model over-cuts.</summary>
    public const double UntrustedModelThreshold = 0.7;

    /// <summary>A null model keeps punctuation-only boundaries (none for untrusted videos); the bitmap is null when none are found.</summary>
    public static (string Text, byte[]? Boundaries) Prepare(string rawText, SpeechBoundaryModel? model = null)
    {
        var normalised = Normalise(rawText);
        var lines = normalised.Split('\n');
        var cleaned = lines.Select(SpeechLineCleaner.Clean).ToArray();
        var trusted = HasTrustedPunctuation(cleaned);
        if (!trusted && model == null)
            return (normalised, null);

        var bits = PunctuationBoundaries(cleaned);
        if (model != null)
        {
            // The model was trained on punctuation-stripped lines, so it only ever sees that form.
            var set = LineBreakFeatureExtractor.Extract(cleaned.Select(StripPunctuation).ToArray());
            if (set == null)
            {
                if (!trusted)
                    return (normalised, null);
            }
            else
            {
                var threshold = trusted ? TrustedModelThreshold : UntrustedModelThreshold;
                foreach (var lineBreak in set.Breaks)
                    if (model.Predict(lineBreak) >= threshold)
                        bits[lineBreak.PrevLine] = true;
            }
        }

        return (SpeechTextAssembler.Assemble(lines, bits), SpeechTextAssembler.ToBytes(bits));
    }
}
