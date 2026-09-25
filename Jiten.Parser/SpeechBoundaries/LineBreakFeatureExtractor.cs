using Jiten.Parser.Runtime;

namespace Jiten.Parser.SpeechBoundaries;

/// <summary>Features for deciding whether a line break in subtitle-style text ends a sentence; training export and inference must both go through here.</summary>
public static class LineBreakFeatureExtractor
{
    public static readonly string[] CategoricalNames =
    [
        "p1_pos", "p1_ctype", "p1_cform", "p1_surf",
        "p2_pos", "p2_cform", "p2_surf",
        "n1_pos", "n1_cform", "n1_surf",
        "n2_pos", "n2_surf",
        "p_first_char", "p_last_char", "n_first_char", "n_last_char", "p_last_char_before_arrow"
    ];

    public static readonly string[] NumericNames =
    [
        "p_len", "n_len", "p_tokens", "n_tokens", "p_bracket_depth", "n_bracket_depth",
        "doc_terminal_share", "doc_comma_share", "doc_mean_len"
    ];

    /// <summary>A break between two kept lines; line indexes point into the raw lines, so lines dropped by cleaning sit between them.</summary>
    public sealed record LineBreak(int PrevLine, int NextLine, string[] Categorical, float[] Numeric);

    /// <param name="CleanedLines">One entry per raw line, empty where cleaning left nothing spoken.</param>
    public sealed record LineBreakSet(string[] CleanedLines, LineBreak[] Breaks);

    private readonly record struct Token(string Surface, string Pos1, string Pos2, string ConjType, string ConjForm);

    // Subtitles end a cue with an arrow or dash when the same speaker carries on, whether or not the sentence has ended.
    private static readonly char[] TrailingContinuationChars = ['➡', '→', '―', '—', ' ', '　'];

    private static readonly HashSet<string> FunctionalPos =
        ["助詞", "助動詞", "接尾辞", "感動詞", "接続詞", "代名詞", "副詞", "連体詞"];

    /// <param name="rawLines">Stored text split on '\n'; each line goes through <see cref="SpeechLineCleaner.Clean"/> first.</param>
    /// <returns>One break between each pair of consecutive kept lines, or null when Sudachi's tokens cannot be mapped back onto the lines.</returns>
    public static LineBreakSet? Extract(IReadOnlyList<string> rawLines)
    {
        var cleaned = rawLines.Select(SpeechLineCleaner.Clean).ToArray();
        var kept = Enumerable.Range(0, cleaned.Length).Where(i => cleaned[i].Length > 0).ToArray();
        if (kept.Length < 2)
            return new LineBreakSet(cleaned, []);

        var keptLines = kept.Select(i => cleaned[i]).ToArray();
        var tokens = TokeniseLines(keptLines);
        if (tokens == null)
            return null;

        var style = DocumentStyle.Of(keptLines);
        var breaks = new LineBreak[kept.Length - 1];
        for (int i = 0; i < breaks.Length; i++)
            breaks[i] = Build(kept[i], kept[i + 1], keptLines[i], keptLines[i + 1], tokens[i], tokens[i + 1], style);

        return new LineBreakSet(cleaned, breaks);
    }

    private static List<Token>[]? TokeniseLines(string[] lines)
    {
        var perLine = new List<Token>[lines.Length];
        for (int i = 0; i < lines.Length; i++)
            perLine[i] = [];

        var settings = ParserRuntimeSettings.Current;
        SudachiInterop.ProcessTextStreaming(settings.SudachiConfigPath, string.Join('\n', lines), settings.DictionaryPath,
                                            out var raw, captureRaw: true, mode: 'B');
        if (string.IsNullOrEmpty(raw))
            return perLine;

        // Sudachi analyses each input line separately but drops the EOS markers, so tokens are mapped back by filtered length.
        var lineLengths = lines.Select(l => SudachiInterop.FilterAllowedChars(l).Length).ToArray();
        int line = 0;
        int consumed = 0;

        foreach (var rawLine in raw.Split('\n'))
        {
            if (rawLine.Length == 0)
                continue;

            var fields = rawLine.Split('\t');
            if (fields.Length < 2)
                return null;

            var surface = fields[0];
            if (surface.Length == 0)
                continue;

            var pos = fields[1].Split(',');
            if (pos.Length < 6)
                return null;

            while (line < lines.Length && consumed == lineLengths[line])
            {
                line++;
                consumed = 0;
            }

            if (line == lines.Length)
                return null;

            consumed += surface.Length;
            if (consumed > lineLengths[line])
                return null;

            perLine[line].Add(new Token(surface, pos[0], pos[1], Blank(pos[4]), Blank(pos[5])));
        }

        for (int i = line; i < lines.Length; i++)
        {
            if ((i == line ? consumed : 0) != lineLengths[i])
                return null;
        }

        return perLine;
    }

    /// <summary>Whether a text punctuates its sentence ends at all changes what an unpunctuated line end means.</summary>
    private readonly record struct DocumentStyle(float TerminalShare, float CommaShare, float MeanLength)
    {
        public static DocumentStyle Of(string[] lines)
        {
            int terminal = 0, comma = 0, nonEmpty = 0;
            long length = 0;
            foreach (var line in lines)
            {
                if (line.Length == 0)
                    continue;

                nonEmpty++;
                length += line.Length;
                if (line[^1] is '。' or '！' or '？' or '!' or '?' or '｡' or '」' or '』')
                    terminal++;
                else if (line[^1] is '、' or '，' or ',')
                    comma++;
            }

            return nonEmpty == 0
                ? new DocumentStyle(0, 0, 0)
                : new DocumentStyle((float)terminal / nonEmpty, (float)comma / nonEmpty, (float)length / nonEmpty);
        }
    }

    private static LineBreak Build(int prevLine, int nextLine, string prev, string next, List<Token> prevTokens, List<Token> nextTokens,
                                   DocumentStyle style)
    {
        var prevContent = prevTokens.Where(IsContent).ToList();
        var nextContent = nextTokens.Where(IsContent).ToList();

        var p1 = prevContent.Count > 0 ? prevContent[^1] : (Token?)null;
        var p2 = prevContent.Count > 1 ? prevContent[^2] : (Token?)null;
        var n1 = nextContent.Count > 0 ? nextContent[0] : (Token?)null;
        var n2 = nextContent.Count > 1 ? nextContent[1] : (Token?)null;

        string[] categorical =
        [
            PosKey(p1), p1?.ConjType ?? "", p1?.ConjForm ?? "", FunctionalSurface(p1),
            PosKey(p2), p2?.ConjForm ?? "", FunctionalSurface(p2),
            PosKey(n1), n1?.ConjForm ?? "", FunctionalSurface(n1),
            PosKey(n2), FunctionalSurface(n2),
            CharClass(prev, first: true), CharClass(prev, first: false),
            CharClass(next, first: true), CharClass(next, first: false),
            CharClass(prev.TrimEnd(TrailingContinuationChars), first: false)
        ];

        float[] numeric =
        [
            prev.Length, next.Length, prevContent.Count, nextContent.Count,
            BracketDepth(prev), BracketDepth(next),
            style.TerminalShare, style.CommaShare, style.MeanLength
        ];

        return new LineBreak(prevLine, nextLine, categorical, numeric);
    }

    private static bool IsContent(Token t) => t.Pos1 is not ("空白" or "補助記号");

    private static string Blank(string field) => field == "*" ? "" : field;

    private static string PosKey(Token? t) => t is { } tok ? $"{tok.Pos1}-{tok.Pos2}" : "";

    private static string FunctionalSurface(Token? t) =>
        t is { } tok && FunctionalPos.Contains(tok.Pos1) ? tok.Surface : "";

    /// <summary>Hiragana and punctuation keep their identity; other scripts collapse to a class so rare characters don't fragment the category.</summary>
    private static string CharClass(string line, bool first)
    {
        if (line.Length == 0)
            return "";

        char c = first ? line[0] : line[^1];
        return c switch
        {
            >= 'ぁ' and <= 'ゖ' => c.ToString(),
            'ー' or 'ッ' => c.ToString(),
            >= 'ァ' and <= 'ヺ' => "KATA",
            >= '一' and <= '龯' or '々' => "KANJI",
            >= '0' and <= '9' or >= '０' and <= '９' => "DIGIT",
            >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ' => "LATIN",
            _ => c.ToString()
        };
    }

    private static float BracketDepth(string line)
    {
        int depth = 0;
        foreach (char c in line)
        {
            if (c is '「' or '『' or '（' or '(' or '【' or '〈' or '《')
                depth++;
            else if (c is '」' or '』' or '）' or ')' or '】' or '〉' or '》')
                depth--;
        }

        return depth;
    }
}
