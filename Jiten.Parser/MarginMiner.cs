using System.Text;
using Jiten.Parser.Runtime;

namespace Jiten.Parser;

/// <summary>
/// Mines low-margin (uncertain-segmentation) regions from raw text using the Sudachi lattice
/// margin export (FFI v3). Pure tokenizer pass — no DB/Redis/JMDict needed — so it can sweep
/// large corpora quickly. Low margins flag near-tied alternative segmentations: candidate
/// hard cases for the eval set, penalty calibration, and user_dic cost measurement.
/// </summary>
public static class MarginMiner
{
    public sealed record MarginFinding(
        int MinMargin,
        long Occurrences,
        string AmbiguousSpan,
        string ExampleSentence,
        string Segmentation);

    /// <summary>
    /// Incremental mining session with bounded memory, suitable for multi-GB corpora.
    /// Feed sentences via <see cref="Process"/>; pull current top findings any time via
    /// <see cref="Snapshot"/>. Sentence dedup uses 64-bit hashes (cleared when the set hits
    /// <see cref="DedupCapacity"/>), and the ambiguity-type table prunes high-margin singletons
    /// when it exceeds <see cref="TypePruneThreshold"/>, so counts for rare types are a floor,
    /// not an exact tally.
    /// </summary>
    public sealed class Session(int threshold = 5000, int minMargin = 0)
    {
        private const int DedupCapacity = 50_000_000;
        private const int TypePruneThreshold = 500_000;

        private sealed class TypeStats
        {
            public int Margin;
            public long Count;
            public required string Sentence;
            public required string Segmentation;
        }

        private readonly string _configPath = ParserRuntimeSettings.Current.SudachiConfigPath;
        private readonly string _dictionaryPath = ParserRuntimeSettings.Current.DictionaryPath;
        private readonly HashSet<ulong> _seen = new();
        private readonly Dictionary<string, TypeStats> _types = new();

        public long SentencesSeen { get; private set; }
        public long SentencesTokenized { get; private set; }
        public long DuplicatesSkipped { get; private set; }
        public long TokenizeErrors { get; private set; }
        public long TypesPruned { get; private set; }
        public long DedupResets { get; private set; }
        public long SpacedSentencesCollapsed { get; private set; }
        public int DistinctTypes => _types.Count;

        public void Process(string sentence)
        {
            SentencesSeen++;

            var collapsed = CollapseCharSpacing(sentence);
            if (!ReferenceEquals(collapsed, sentence))
            {
                SpacedSentencesCollapsed++;
                sentence = collapsed;
            }

            if (sentence.Length < 2)
                return;

            if (_seen.Count >= DedupCapacity)
            {
                _seen.Clear();
                DedupResets++;
            }
            if (!_seen.Add(Fnv1A64(sentence)))
            {
                DuplicatesSkipped++;
                return;
            }

            List<WordInfo> tokens;
            try
            {
                tokens = SudachiInterop.ProcessTextStreaming(_configPath, sentence, _dictionaryPath,
                                                             mode: 'B', emitMargins: true);
            }
            catch (Exception)
            {
                TokenizeErrors++;
                return;
            }

            SentencesTokenized++;
            if (tokens.Count == 0)
                return;

            bool InBand(WordInfo t) =>
                t.SudachiBoundaryMargin is { } m && m >= minMargin && m < threshold && ContainsJapaneseLetter(t.Text);

            string? segmentation = null;

            // Group adjacent in-band tokens into one ambiguous span = one ambiguity type
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!InBand(tokens[i]))
                    continue;

                int j = i;
                int spanMin = int.MaxValue;
                var span = new StringBuilder();
                while (j < tokens.Count && InBand(tokens[j]))
                {
                    span.Append(tokens[j].Text).Append('|');
                    spanMin = Math.Min(spanMin, tokens[j].SudachiBoundaryMargin!.Value);
                    j++;
                }
                var key = span.ToString(0, span.Length - 1);

                if (_types.TryGetValue(key, out var stats))
                {
                    stats.Count++;
                    if (spanMin < stats.Margin)
                        stats.Margin = spanMin;
                }
                else
                {
                    segmentation ??= string.Join("|", tokens.Select(t =>
                        InBand(t) ? $"{t.Text}‹{t.SudachiBoundaryMargin}›" : t.Text));
                    _types[key] = new TypeStats
                        { Margin = spanMin, Count = 1, Sentence = sentence, Segmentation = segmentation };
                    if (_types.Count >= TypePruneThreshold)
                        PruneSingletons();
                }

                i = j - 1;
            }
        }

        public List<MarginFinding> Snapshot(int maxFindings) =>
            _types
                .Select(kv => new MarginFinding(kv.Value.Margin, kv.Value.Count, kv.Key,
                                                kv.Value.Sentence, kv.Value.Segmentation))
                .OrderByDescending(f => f.Occurrences)
                .ThenBy(f => f.MinMargin)
                .Take(maxFindings)
                .ToList();

        private void PruneSingletons()
        {
            int target = TypePruneThreshold * 3 / 4;
            var evict = _types
                        .Where(kv => kv.Value.Count == 1)
                        .OrderByDescending(kv => kv.Value.Margin)
                        .Take(_types.Count - target)
                        .Select(kv => kv.Key)
                        .ToList();
            foreach (var key in evict)
                _types.Remove(key);
            TypesPruned += evict.Count;
        }

        // OCR/char-level corpora put a space between every character, which forces a Sudachi
        // boundary at every position and hides all lattice ambiguity (margins never in band).
        // When spaces make up ~half the sentence, treat it as char-spaced and strip them all.
        // Shared with UserDicAuditor, which needs the same treatment for its A/B diff.
        internal static string CollapseCharSpacing(string s)
        {
            int spaces = 0;
            foreach (var c in s)
                if (c is ' ' or '　')
                    spaces++;
            if (spaces == 0 || spaces * 2 < s.Length - 1)
                return s;

            var sb = new StringBuilder(s.Length - spaces);
            foreach (var c in s)
                if (c is not (' ' or '　'))
                    sb.Append(c);
            return sb.ToString();
        }

        private static ulong Fnv1A64(string s)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in s)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash;
        }
    }

    /// <summary>
    /// Convenience wrapper for in-memory text (tests, CLI literal input). For large corpora,
    /// drive a <see cref="Session"/> with <see cref="EnumerateSentences"/> instead.
    /// </summary>
    public static List<MarginFinding> MineText(string text, int threshold = 5000, int maxFindings = 200,
                                               int minMargin = 0, Action<int, int>? progress = null)
    {
        var session = new Session(threshold, minMargin);
        var sentences = SplitSentences(text);
        int processed = 0;
        foreach (var sentence in sentences)
        {
            processed++;
            if (progress != null && processed % 2000 == 0)
                progress(processed, sentences.Count);
            session.Process(sentence);
        }
        return session.Snapshot(maxFindings);
    }

    /// <summary>
    /// Streams sentences out of a reader without ever materializing the whole text: reads in
    /// 64K-char blocks, cuts on 。！？ (ender kept) and newline (dropped), and force-flushes
    /// pathological ender-less runs at 4096 chars so a single-line corpus stays bounded.
    /// </summary>
    public static IEnumerable<string> EnumerateSentences(TextReader reader)
    {
        const int MaxSentenceChars = 4096;
        var buffer = new char[1 << 16];
        var pending = new StringBuilder();
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                bool isEnder = c is '。' or '！' or '？';
                if (isEnder)
                    pending.Append(c);
                else if (c != '\n')
                {
                    pending.Append(c);
                    if (pending.Length < MaxSentenceChars)
                        continue;
                }

                if (isEnder || c == '\n' || pending.Length >= MaxSentenceChars)
                {
                    var s = pending.ToString().Trim();
                    pending.Clear();
                    if (s.Length > 0)
                        yield return s;
                }
            }
        }
        var last = pending.ToString().Trim();
        if (last.Length > 0)
            yield return last;
    }

    private static bool ContainsJapaneseLetter(string s)
    {
        foreach (var c in s)
        {
            if ((c >= 'ぁ' && c <= 'ゖ') || (c >= 'ァ' && c <= 'ヺ') ||
                (c >= '一' && c <= '鿿') || c == '々')
                return true;
        }
        return false;
    }

    private static List<string> SplitSentences(string text)
    {
        using var reader = new StringReader(text);
        return EnumerateSentences(reader).ToList();
    }
}
