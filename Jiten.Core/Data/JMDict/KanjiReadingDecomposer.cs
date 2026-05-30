namespace Jiten.Core.Data.JMDict;

public class KanjiReadingDecomposer
{
    private readonly Dictionary<string, List<string>> _kanjiReadings;

    public KanjiReadingDecomposer(IEnumerable<Kanji> allKanji)
    {
        _kanjiReadings = new Dictionary<string, List<string>>();
        foreach (var kanji in allKanji)
        {
            var readings = new List<string>();
            var rendakuReadings = new List<string>();
            var geminatedReadings = new List<string>();

            foreach (var on in kanji.OnReadings)
            {
                var hira = KatakanaToHiragana(CleanReading(on));
                if (hira.Length > 0) readings.Add(hira);
            }

            foreach (var kun in kanji.KunReadings)
            {
                var clean = CleanReading(kun);
                var dotIndex = clean.IndexOf('.');
                var stem = dotIndex >= 0 ? clean[..dotIndex] : clean;
                if (stem.Length > 0) readings.Add(stem);
            }

            var distinct = readings.Distinct().ToList();
            foreach (var r in distinct)
            {
                var rendaku = ApplyRendaku(r);
                if (rendaku != null) rendakuReadings.Add(rendaku);
                var handakuten = ApplyHandakuten(r);
                if (handakuten != null) rendakuReadings.Add(handakuten);
                var geminated = ApplyGemination(r);
                if (geminated != null) geminatedReadings.Add(geminated);
            }

            distinct.AddRange(rendakuReadings.Distinct());
            distinct.AddRange(geminatedReadings.Distinct());

            _kanjiReadings[kanji.Character] = distinct.Distinct().ToList();
        }
    }

    public int TotalReadingCandidates => _kanjiReadings.Values.Sum(v => v.Count);

    /// <summary>
    /// Decomposes a curated RubyText annotation (e.g. "生[せい]活[かつ]", "巫女[みこ]", "食[た]べ物[もの]")
    /// into (kanji, reading) pairs. Single-kanji ruby groups map directly. Multi-kanji ruby groups
    /// are first split via KANJIDIC backtracking; if that fails the group is a jukujikun (熟字訓) whose
    /// reading is assigned to the whole span — we then credit it only to the kanji whose own reading
    /// set already contains that reading (the "owning" kanji), so passenger kanji like 女 in 巫女 are
    /// not polluted with spurious readings. Returns an empty list when no kanji owns the reading
    /// (true ateji such as 今日/寿司) — never null.
    /// </summary>
    public List<(string KanjiChar, string Reading)> DecomposeFromRuby(string rubyText)
    {
        var pairs = new List<(string, string)>();
        var pendingBase = new System.Text.StringBuilder();

        int i = 0;
        while (i < rubyText.Length)
        {
            char c = rubyText[i];
            if (c == '[')
            {
                int close = rubyText.IndexOf(']', i);
                if (close < 0) break;

                var reading = NormalizeReading(rubyText.Substring(i + 1, close - i - 1));

                // The reading annotates the trailing kanji run of the pending base text;
                // any leading kana in the buffer are literal okurigana.
                var baseStr = pendingBase.ToString();
                int k = baseStr.Length;
                while (k > 0 && IsKanjiChar(baseStr[k - 1])) k--;
                var kanjiRun = baseStr[k..];

                ResolveRubyGroup(kanjiRun, reading, pairs);

                pendingBase.Clear();
                i = close + 1;
            }
            else
            {
                pendingBase.Append(c);
                i++;
            }
        }

        return pairs;
    }

    private void ResolveRubyGroup(string kanjiRun, string reading, List<(string, string)> pairs)
    {
        if (kanjiRun.Length == 0 || reading.Length == 0) return;

        if (kanjiRun.Length == 1)
        {
            pairs.Add((kanjiRun, reading));
            return;
        }

        // Multi-kanji block: prefer a per-kanji split if the readings line up.
        var decomposed = BacktrackDecompose(kanjiRun, reading, 0, 0);
        if (decomposed != null)
        {
            pairs.AddRange(decomposed);
            return;
        }

        // Jukujikun: whole-span reading credited only to kanji that own it.
        foreach (var ch in kanjiRun)
        {
            var chs = ch.ToString();
            if (_kanjiReadings.TryGetValue(chs, out var set) && set.Contains(reading))
                pairs.Add((chs, reading));
        }
    }

    private static string NormalizeReading(string reading)
    {
        var hira = KatakanaToHiragana(reading);
        return new string(hira.Where(IsKana).ToArray());
    }

    public List<(string KanjiChar, string Reading)>? Decompose(string kanjiText, string kanaReading)
    {
        if (string.IsNullOrEmpty(kanjiText) || string.IsNullOrEmpty(kanaReading))
            return null;

        // Expand 々 to preceding kanji
        kanjiText = ExpandRepeatMarker(kanjiText);

        var kana = KatakanaToHiragana(kanaReading);

        // Strip matching prefix/suffix kana
        int prefixLen = 0;
        while (prefixLen < kanjiText.Length && prefixLen < kana.Length
               && IsKana(kanjiText[prefixLen])
               && KanaEqual(kanjiText[prefixLen], kana[prefixLen]))
            prefixLen++;

        int suffixLen = 0;
        while (suffixLen < kanjiText.Length - prefixLen
               && suffixLen < kana.Length - prefixLen
               && IsKana(kanjiText[kanjiText.Length - 1 - suffixLen])
               && KanaEqual(kanjiText[kanjiText.Length - 1 - suffixLen], kana[kana.Length - 1 - suffixLen]))
            suffixLen++;

        var midKanji = kanjiText.AsSpan(prefixLen, kanjiText.Length - prefixLen - suffixLen);
        var midKana = kana.AsSpan(prefixLen, kana.Length - prefixLen - suffixLen);

        if (midKanji.Length == 0 || midKana.Length == 0)
            return [];

        // Identify segments: alternating kanji runs and kana separators
        var segments = IdentifySegments(midKanji);
        if (segments.Count == 0)
            return null;

        // Split reading by kana anchors
        var kanjiRuns = segments.Where(s => s.IsKanji).ToList();
        var kanaAnchors = segments.Where(s => !s.IsKanji).Select(s => s.Text).ToList();

        var readingParts = SplitByAnchors(midKana.ToString(), kanaAnchors);
        if (readingParts == null || readingParts.Count != kanjiRuns.Count)
            return null;

        // Resolve each kanji run
        var results = new List<(string, string)>();
        for (int i = 0; i < kanjiRuns.Count; i++)
        {
            var run = kanjiRuns[i].Text;
            var reading = readingParts[i];

            if (reading.Length == 0)
                return null;

            if (run.Length == 1)
            {
                var cleanedReading = new string(reading.Where(IsKana).ToArray());
                if (cleanedReading.Length == 0)
                    return null;
                results.Add((run, cleanedReading));
            }
            else
            {
                var decomposed = BacktrackDecompose(run, reading, 0, 0);
                if (decomposed == null)
                    return null;
                results.AddRange(decomposed);
            }
        }

        return results;
    }

    private List<(string, string)>? BacktrackDecompose(string kanjiBlock, string reading, int kanjiPos, int readingPos)
    {
        if (kanjiPos == kanjiBlock.Length && readingPos == reading.Length)
            return [];
        if (kanjiPos >= kanjiBlock.Length || readingPos >= reading.Length)
            return null;

        var kanjiChar = kanjiBlock[kanjiPos].ToString();

        if (!_kanjiReadings.TryGetValue(kanjiChar, out var candidates))
            return null;

        var remainingReading = reading.AsSpan(readingPos);
        foreach (var candidate in candidates)
        {
            if (remainingReading.StartsWith(candidate))
            {
                var rest = BacktrackDecompose(kanjiBlock, reading, kanjiPos + 1, readingPos + candidate.Length);
                if (rest != null)
                {
                    rest.Insert(0, (kanjiChar, candidate));
                    return rest;
                }
            }
        }

        return null;
    }

    private static List<Segment> IdentifySegments(ReadOnlySpan<char> text)
    {
        var segments = new List<Segment>();
        int i = 0;
        while (i < text.Length)
        {
            if (IsKanjiChar(text[i]))
            {
                int start = i;
                while (i < text.Length && IsKanjiChar(text[i])) i++;
                segments.Add(new Segment(text[start..i].ToString(), true));
            }
            else
            {
                int start = i;
                while (i < text.Length && !IsKanjiChar(text[i])) i++;
                segments.Add(new Segment(text[start..i].ToString(), false));
            }
        }
        return segments;
    }

    private static List<string>? SplitByAnchors(string reading, List<string> anchors)
    {
        if (anchors.Count == 0)
            return [reading];

        var parts = new List<string>();
        var remaining = KatakanaToHiragana(reading);

        foreach (var anchor in anchors)
        {
            var normalizedAnchor = KatakanaToHiragana(anchor);
            int idx = remaining.IndexOf(normalizedAnchor, StringComparison.Ordinal);
            if (idx == -1)
                return null;
            parts.Add(remaining[..idx]);
            remaining = remaining[(idx + normalizedAnchor.Length)..];
        }
        parts.Add(remaining);

        return parts;
    }

    private static string ExpandRepeatMarker(string text)
    {
        if (!text.Contains('々'))
            return text;

        var chars = text.ToCharArray();
        for (int i = 1; i < chars.Length; i++)
        {
            if (chars[i] == '々' && IsKanjiChar(chars[i - 1]))
                chars[i] = chars[i - 1];
        }
        return new string(chars);
    }

    private static string? ApplyRendaku(string reading)
    {
        if (reading.Length == 0) return null;
        char first = reading[0];
        char? voiced = first switch
        {
            'か' => 'が', 'き' => 'ぎ', 'く' => 'ぐ', 'け' => 'げ', 'こ' => 'ご',
            'さ' => 'ざ', 'し' => 'じ', 'す' => 'ず', 'せ' => 'ぜ', 'そ' => 'ぞ',
            'た' => 'だ', 'ち' => 'ぢ', 'つ' => 'づ', 'て' => 'で', 'と' => 'ど',
            'は' => 'ば', 'ひ' => 'び', 'ふ' => 'ぶ', 'へ' => 'べ', 'ほ' => 'ぼ',
            _ => null
        };
        if (voiced == null) return null;
        return voiced + reading[1..];
    }

    private static string? ApplyHandakuten(string reading)
    {
        if (reading.Length == 0) return null;
        char first = reading[0];
        char? hd = first switch
        {
            'は' => 'ぱ', 'ひ' => 'ぴ', 'ふ' => 'ぷ', 'へ' => 'ぺ', 'ほ' => 'ぽ',
            _ => null
        };
        if (hd == null) return null;
        return hd + reading[1..];
    }

    private static string? ApplyGemination(string reading)
    {
        if (reading.Length < 2) return null;
        char last = reading[^1];
        if (last is 'く' or 'き' or 'ち' or 'つ')
            return reading[..^1] + 'っ';
        return null;
    }

    private static string CleanReading(string reading)
    {
        var cleaned = new string(reading.Where(c => IsKana(c) || c == '.' || c is >= 'ァ' and <= 'ヶ').ToArray());
        return cleaned;
    }

    private static string KatakanaToHiragana(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'ァ' and <= 'ヶ')
                chars[i] = (char)(chars[i] - 0x60);
        }
        return new string(chars);
    }

    private static bool IsKana(char c) => JapaneseTextHelper.IsKana(c);

    private static bool IsKanjiChar(char c) => JapaneseTextHelper.IsKanji(c);

    private static char ToHiragana(char c) => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c;

    private static bool KanaEqual(char a, char b) => ToHiragana(a) == ToHiragana(b);

    private record Segment(string Text, bool IsKanji);
}
