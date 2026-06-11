using System.Text;
using Jiten.Parser.Runtime;

namespace Jiten.Parser;

/// <summary>
/// Finds user dictionary entries that capture text across genuine word boundaries by
/// tokenizing sentences twice — with and without the user dictionary — and flagging
/// user-dic tokens whose start offset falls strictly inside a token of the no-user-dic
/// parse (e.g. 彼女|の|手 → 彼|[女の手]). Benign user-dic firings start on an existing
/// boundary (お|尻|の|穴 → お|[尻の穴]) and are not reported. Pure tokenizer pass, no DB.
/// </summary>
public static class UserDicAuditor
{
    public sealed record AuditFinding(
        string Entry,
        long Occurrences,
        int DistinctCrossedTokens,
        string CrossedTokens,
        string ExampleSentence,
        string WithDic,
        string WithoutDic);

    public sealed class Session
    {
        private const int MaxCrossedTokensTracked = 20;

        private sealed class EntryStats
        {
            public long Count;
            public required HashSet<string> Crossed;
            public required string Sentence;
            public required string WithDic;
            public required string WithoutDic;
        }

        private readonly string _configPath = ParserRuntimeSettings.Current.SudachiConfigPath;
        private readonly string _noUserDicConfigPath = ParserRuntimeSettings.Current.SudachiNoUserDicConfigPath;
        private readonly string _dictionaryPath = ParserRuntimeSettings.Current.DictionaryPath;
        private readonly HashSet<string> _surfaces;
        private readonly HashSet<ulong> _seen = new();
        private readonly Dictionary<string, EntryStats> _entries = new();

        public long SentencesSeen { get; private set; }
        public long SentencesTokenized { get; private set; }
        public long DuplicatesSkipped { get; private set; }
        public long TokenizeErrors { get; private set; }
        public long Captures { get; private set; }
        public long SpacedSentencesCollapsed { get; private set; }
        public int DistinctEntries => _entries.Count;

        public Session(string userDicXmlPath)
        {
            _surfaces = LoadSurfaces(userDicXmlPath);
        }

        private static HashSet<string> LoadSurfaces(string xmlPath)
        {
            // user_dic.xml is Sudachi CSV: surface,leftId,rightId,cost,display,pos1,...
            // Tokens in output carry the original surface text, which may match either
            // the lookup surface (col 0, lowercased) or the display form (col 4).
            var surfaces = new HashSet<string>();
            foreach (var line in File.ReadLines(xmlPath))
            {
                var firstComma = line.IndexOf(',');
                if (firstComma <= 0)
                    continue;
                surfaces.Add(line[..firstComma]);

                int idx = firstComma, commas = 1;
                while (commas < 5 && idx >= 0)
                {
                    idx = line.IndexOf(',', idx + 1);
                    commas++;
                }
                if (commas == 5 && idx > 0)
                {
                    var start = line.LastIndexOf(',', idx - 1) + 1;
                    if (idx > start)
                        surfaces.Add(line[start..idx]);
                }
            }
            return surfaces;
        }

        public void Process(string sentence)
        {
            SentencesSeen++;

            var collapsed = MarginMiner.Session.CollapseCharSpacing(sentence);
            if (!ReferenceEquals(collapsed, sentence))
            {
                SpacedSentencesCollapsed++;
                sentence = collapsed;
            }

            if (sentence.Length < 2)
                return;

            if (!_seen.Add(Fnv1A64(sentence)))
            {
                DuplicatesSkipped++;
                return;
            }
            if (_seen.Count >= 50_000_000)
                _seen.Clear();

            List<WordInfo> withDic, withoutDic;
            try
            {
                withDic = SudachiInterop.ProcessTextStreaming(_configPath, sentence, _dictionaryPath, mode: 'B');
                withoutDic = SudachiInterop.ProcessTextStreaming(_noUserDicConfigPath, sentence, _dictionaryPath, mode: 'B');
            }
            catch (Exception)
            {
                TokenizeErrors++;
                return;
            }

            SentencesTokenized++;

            // Cumulative char offsets; raw Sudachi output covers the input text in order.
            var offsetsB = Offsets(withDic);
            var offsetsA = Offsets(withoutDic);

            var startsA = new HashSet<int>();
            foreach (var (start, _) in offsetsA)
                startsA.Add(start);

            for (int i = 0; i < withDic.Count; i++)
            {
                var token = withDic[i];
                var (startB, endB) = offsetsB[i];
                if (startB == 0 || startsA.Contains(startB) || !_surfaces.Contains(token.Text))
                    continue;

                // Started strictly inside a no-user-dic token: boundary-crossing capture
                int crossedIdx = offsetsA.FindIndex(o => o.Start < startB && o.End > startB);
                if (crossedIdx < 0)
                    continue;

                Captures++;
                var crossedText = withoutDic[crossedIdx].Text;

                if (_entries.TryGetValue(token.Text, out var stats))
                {
                    stats.Count++;
                    if (stats.Crossed.Count < MaxCrossedTokensTracked)
                        stats.Crossed.Add(crossedText);
                }
                else
                {
                    _entries[token.Text] = new EntryStats
                    {
                        Count = 1,
                        Crossed = [crossedText],
                        Sentence = sentence,
                        WithDic = RegionSeg(withDic, offsetsB, startB, endB),
                        WithoutDic = RegionSeg(withoutDic, offsetsA, startB, endB),
                    };
                }
            }
        }

        public List<AuditFinding> Snapshot(int maxFindings) =>
            _entries
                .Select(kv => new AuditFinding(kv.Key, kv.Value.Count, kv.Value.Crossed.Count,
                                               string.Join("、", kv.Value.Crossed.Take(8)),
                                               kv.Value.Sentence, kv.Value.WithDic, kv.Value.WithoutDic))
                .OrderByDescending(f => f.Occurrences)
                .Take(maxFindings)
                .ToList();

        private static List<(int Start, int End)> Offsets(List<WordInfo> tokens)
        {
            var result = new List<(int, int)>(tokens.Count);
            int pos = 0;
            foreach (var t in tokens)
            {
                result.Add((pos, pos + t.Text.Length));
                pos += t.Text.Length;
            }
            return result;
        }

        private static string RegionSeg(List<WordInfo> tokens, List<(int Start, int End)> offsets, int start, int end)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (offsets[i].End <= start - 6 || offsets[i].Start >= end + 6)
                    continue;
                if (sb.Length > 0)
                    sb.Append('|');
                sb.Append(tokens[i].Text);
            }
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
}
