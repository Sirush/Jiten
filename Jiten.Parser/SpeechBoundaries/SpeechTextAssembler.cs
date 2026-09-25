using System.Collections;
using System.Text;

namespace Jiten.Parser.SpeechBoundaries;

/// <summary>Turns subtitle-style stored text into one sentence per line, so the prose sentence splitter can cut it unchanged.</summary>
public static class SpeechTextAssembler
{
    public const double BoundaryThreshold = 0.5;

    private static readonly char[] SentenceEnders = ['。', '！', '？', '…', '」', '』'];

    /// <returns>Bit i set when a sentence ends after raw line i; bits of lines dropped by cleaning are never set.</returns>
    public static BitArray DetectBoundaries(IReadOnlyList<string> rawLines, SpeechBoundaryModel model)
    {
        var bits = new BitArray(rawLines.Count);
        var set = LineBreakFeatureExtractor.Extract(rawLines);
        if (set == null)
        {
            for (int i = 0; i < rawLines.Count; i++)
                bits[i] = SpeechLineCleaner.Clean(rawLines[i]).Length > 0;
            return bits;
        }

        foreach (var lineBreak in set.Breaks)
            bits[lineBreak.PrevLine] = model.Predict(lineBreak) >= BoundaryThreshold;

        var lastKept = Array.FindLastIndex(set.CleanedLines, l => l.Length > 0);
        if (lastKept >= 0)
            bits[lastKept] = true;

        return bits;
    }

    /// <summary>Assembles stored subtitle text, reusing its stored bitmap when it still matches the text's line count.</summary>
    /// <returns>The text to parse, and the bitmap to store back.</returns>
    public static (string Text, byte[] Boundaries) Prepare(string rawText, byte[]? storedBoundaries, SpeechBoundaryModel model)
    {
        var lines = rawText.Split('\n');
        var bits = FromBytes(storedBoundaries, lines.Length) ?? DetectBoundaries(lines, model);
        return (Assemble(lines, bits), ToBytes(bits));
    }

    public static BitArray? FromBytes(byte[]? bytes, int lineCount) =>
        bytes != null && bytes.Length == (lineCount + 7) / 8 ? new BitArray(bytes) { Length = lineCount } : null;

    public static byte[] ToBytes(BitArray bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        bits.CopyTo(bytes, 0);
        return bytes;
    }

    public static string Assemble(IReadOnlyList<string> rawLines, BitArray boundaries)
    {
        var sb = new StringBuilder();
        var sentence = new StringBuilder();
        for (int i = 0; i < rawLines.Count; i++)
        {
            var text = SpeechLineCleaner.ToSentenceText(SpeechLineCleaner.Clean(rawLines[i]));
            if (text.Length == 0)
                continue;

            sentence.Append(text);
            if (i < boundaries.Length && !boundaries[i])
                continue;

            if (Array.IndexOf(SentenceEnders, sentence[^1]) < 0)
                sentence.Append('。');
            sb.Append(sentence).Append('\n');
            sentence.Clear();
        }

        if (sentence.Length > 0)
            sb.Append(sentence).Append('。').Append('\n');

        return sb.ToString();
    }
}
