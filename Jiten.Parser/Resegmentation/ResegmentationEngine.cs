using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Data;
using Jiten.Parser.Diagnostics;
using Jiten.Parser.Scoring;

namespace Jiten.Parser.Resegmentation;

internal static class ResegmentationEngine
{
    private const int MinAcceptScore = 25;
    private const int MinAcceptScoreConfidence = 50;

    public static void TryImproveUncertainSpans(
        List<SentenceInfo> sentences,
        Dictionary<string, List<int>> lookups,
        Dictionary<int, int> frequencyRanks,
        Dictionary<int, JmDictWordMeta> wordMeta,
        HashSet<string>? protectedSurfaces = null,
        ParserDiagnostics? diagnostics = null)
    {
        var pending = new List<(SentenceInfo sentence, UncertainSpan span, SpanPath path, PartOfSpeech? prevPos, PartOfSpeech? nextPos)>();

        foreach (var sentence in sentences)
        {
            var spans = UncertaintyDetector.FindSpans(sentence, lookups, wordMeta, protectedSurfaces);
            if (spans.Count == 0) continue;

            foreach (var span in spans.OrderByDescending(s => s.WordIndex))
            {
                var word = sentence.Words[span.WordIndex].word;
                bool isCompoundNumeral = word.PartOfSpeechSection1 == PartOfSpeechSection.Numeral && word.Text.Length > 1;

                SpanPath? path;
                if (isCompoundNumeral)
                {
                    path = TrySplitCompoundNumeral(span.Text, lookups, wordMeta);
                    if (path == null)
                        continue;
                }
                else
                {
                    path = ResegmentationScorer.FindBestPath(span.Text, lookups, frequencyRanks,
                                                             forbidFullSpanEdge: span.NameOnly,
                                                             wordMeta: wordMeta);
                    var rejection = path switch
                    {
                        null => "no path found",
                        _ when !path.IsComplete(span.Text.Length) => "path incomplete",
                        _ when path.Segments.Count <= 1 => "single segment",
                        _ when path.Segments.Count > (span.Text.Length + 1) / 2 => "too fragmented",
                        _ when HasBadSingleKana(path, span.Text) => "single-kana segment",
                        _ when IsReduplicatedKanaSplit(path, span.Text, wordMeta) => "reduplicated mimetic",
                        _ when IsKatakanaConjugatedWordSpelling(span.Text, lookups, wordMeta) =>
                            "katakana conjugated-word spelling",
                        _ when HasSuruConjugationTail(path, span.Text) => "suru-conjugation tail",
                        _ when HasShortPureNameSegment(path, wordMeta) => "short pure-name segment",
                        _ when HasNameSegmentOutsidePersonContext(path, span, sentence, wordMeta) =>
                            "name segment outside person context",
                        _ when ResegmentationScorer.ScorePath(path, frequencyRanks, span.Text) < 0 => "negative path score",
                        _ => null
                    };
                    if (rejection != null)
                    {
                        if (path != null)
                            diagnostics?.LogParserEvent(
                                "Resegmentation.UncertainSpan", "rejected", [span.Text],
                                path.Segments.Select(s => span.Text.Substring(s.StartChar, s.Length)).ToArray(),
                                rejection);
                        continue;
                    }
                }

                var prevPos = span.WordIndex > 0 ? sentence.Words[span.WordIndex - 1].word.PartOfSpeech : (PartOfSpeech?)null;
                var nextPos = span.WordIndex < sentence.Words.Count - 1 ? sentence.Words[span.WordIndex + 1].word.PartOfSpeech : (PartOfSpeech?)null;

                pending.Add((sentence, span, path, prevPos, nextPos));
            }
        }

        if (pending.Count == 0) return;

        var allWordIds = pending.SelectMany(p => p.path.Segments.SelectMany(s => s.WordIds)).Distinct();
        var wordPosByWordId = new Dictionary<int, PartOfSpeech>();
        foreach (var id in allWordIds)
        {
            if (wordMeta.TryGetValue(id, out var meta))
                wordPosByWordId[id] = meta.GetPrimaryPos();
        }

        foreach (var (sentence, span, path, prevPos, nextPos) in pending)
        {
            var freqScore = ResegmentationScorer.ScorePath(path, frequencyRanks, span.Text);
            var posScore  = ResegmentationScorer.ScorePosTransitions(path, wordPosByWordId, prevPos, nextPos, frequencyRanks);
            if (freqScore + posScore < MinAcceptScore)
                continue;
            ReplaceSpan(sentence, span, path, lookups, frequencyRanks, wordMeta,
                        "Resegmentation.UncertainSpan", freqScore, posScore, diagnostics);
        }
    }

    public static bool TryResegmentLowConfidenceTokens(
        List<SentenceInfo> sentences,
        Dictionary<string, List<int>> lookups,
        Dictionary<int, int> frequencyRanks,
        Dictionary<(int sentenceIndex, int wordIndex), int?> marginMap,
        Dictionary<int, JmDictWordMeta> wordMeta,
        ParserDiagnostics? diagnostics = null)
    {
        var pending = new List<(SentenceInfo sentence, UncertainSpan span, SpanPath path, PartOfSpeech? prevPos, PartOfSpeech? nextPos)>();

        for (int si = 0; si < sentences.Count; si++)
        {
            var sentence = sentences[si];
            for (int wi = sentence.Words.Count - 1; wi >= 0; wi--)
            {
                var (word, _, _) = sentence.Words[wi];

                if (word.Text.Length < 3 || word.Text.Length > 14)
                    continue;
                if (word.PreMatchedWordId != null)
                    continue;
                if (Array.IndexOf(UncertaintyDetector.SkipPos, word.PartOfSpeech) >= 0)
                    continue;
                if (PosMapper.IsNameLikeSudachiNoun(word.PartOfSpeech, word.PartOfSpeechSection1,
                        word.PartOfSpeechSection2, word.PartOfSpeechSection3))
                    continue;
                if (!marginMap.TryGetValue((si, wi), out var margin) || !ScoringPolicy.IsLowConfidence(margin))
                    continue;

                var path = ResegmentationScorer.FindBestPath(word.Text, lookups, frequencyRanks,
                                                             wordMeta: wordMeta);
                if (path == null || !path.IsComplete(word.Text.Length) || path.Segments.Count <= 1)
                    continue;
                if (path.Segments.Count > (word.Text.Length + 1) / 2)
                    continue;
                if (HasBadSingleKana(path, word.Text))
                    continue;
                if (IsReduplicatedKanaSplit(path, word.Text, wordMeta))
                    continue;
                if (IsKatakanaConjugatedWordSpelling(word.Text, lookups, wordMeta))
                    continue;
                if (HasSuruConjugationTail(path, word.Text))
                    continue;
                if (HasShortPureNameSegment(path, wordMeta))
                    continue;
                if (ResegmentationScorer.ScorePath(path, frequencyRanks, word.Text) < MinAcceptScoreConfidence)
                    continue;

                var prevPos = wi > 0 ? sentence.Words[wi - 1].word.PartOfSpeech : (PartOfSpeech?)null;
                var nextPos = wi < sentence.Words.Count - 1 ? sentence.Words[wi + 1].word.PartOfSpeech : (PartOfSpeech?)null;

                pending.Add((sentence, new UncertainSpan
                {
                    WordIndex = wi,
                    Text      = word.Text,
                    Position  = sentence.Words[wi].position,
                    Length    = sentence.Words[wi].length
                }, path, prevPos, nextPos));
            }
        }

        if (pending.Count == 0) return false;

        var allWordIds = pending.SelectMany(p => p.path.Segments.SelectMany(s => s.WordIds)).Distinct();
        var wordPosByWordId = new Dictionary<int, PartOfSpeech>();
        foreach (var id in allWordIds)
        {
            if (wordMeta.TryGetValue(id, out var meta))
                wordPosByWordId[id] = meta.GetPrimaryPos();
        }

        bool anyApplied = false;
        foreach (var (sentence, span, path, prevPos, nextPos) in pending)
        {
            var freqScore = ResegmentationScorer.ScorePath(path, frequencyRanks, span.Text);
            var posScore  = ResegmentationScorer.ScorePosTransitions(path, wordPosByWordId, prevPos, nextPos, frequencyRanks);
            if (freqScore + posScore < MinAcceptScoreConfidence)
                continue;
            ReplaceSpan(sentence, span, path, lookups, frequencyRanks, wordMeta,
                        "Resegmentation.LowConfidence", freqScore, posScore, diagnostics);
            anyApplied = true;
        }

        return anyApplied;
    }

    // Splits at the last place marker (五十七 → 五十+七); a piece JMnedict has only as a name (二十三) splits further.
    private static SpanPath? TrySplitCompoundNumeral(string text, Dictionary<string, List<int>> lookups,
        Dictionary<int, JmDictWordMeta> wordMeta)
    {
        for (int i = text.Length - 1; i >= 1; i--)
        {
            if (text[i - 1] is not ('十' or '百' or '千' or '万' or '億' or '兆'))
                continue;

            var left = text[..i];
            var right = text[i..];

            var leftIds = NonNameLookupIds(left, lookups, wordMeta);
            if (leftIds == null)
                continue;

            var rightIds = NonNameLookupIds(right, lookups, wordMeta);
            if (rightIds != null)
                return new SpanPath([
                    new SpanTokenCandidate(0, left.Length, leftIds),
                    new SpanTokenCandidate(i, right.Length, rightIds)
                ]);

            var sub = TrySplitCompoundNumeral(right, lookups, wordMeta);
            if (sub != null)
                return new SpanPath([
                    new SpanTokenCandidate(0, left.Length, leftIds),
                    .. sub.Segments.Select(s => new SpanTokenCandidate(i + s.StartChar, s.Length, s.WordIds)),
                ]);
        }

        return null;
    }

    // Drops all-Name/Unknown entries, including JMnedict "unclass" (五十五 "Isoi"), unlike IsTrueName.
    private static List<int>? NonNameLookupIds(string text, Dictionary<string, List<int>> lookups,
        Dictionary<int, JmDictWordMeta> wordMeta)
    {
        if (!lookups.TryGetValue(text, out var ids) || ids.Count == 0)
            return null;
        var filtered = ids.Where(id => !wordMeta.TryGetValue(id, out var meta)
                                       || meta.Pos.Length == 0
                                       || !meta.Pos.All(p => p is PartOfSpeech.Name or PartOfSpeech.Unknown)).ToList();
        return filtered.Count > 0 ? filtered : null;
    }

    // Short pure-name segments shred OOV names (ファルマ → ファ+ルマ); long ones are real (ラムシャーリー → ラム+シャーリー).
    private static bool HasShortPureNameSegment(SpanPath path, Dictionary<int, JmDictWordMeta> wordMeta)
    {
        foreach (var s in path.Segments)
        {
            if (s.Length > 2)
                continue;

            // IsTrueName, since JMnedict "unclass" entries cover real slang like ダサ.
            bool anyName = false, allNameLike = true;
            foreach (var id in s.WordIds)
            {
                if (!wordMeta.TryGetValue(id, out var meta)) continue;
                if (meta.IsTrueName)
                    anyName = true;
                if (meta.Pos.Any(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown)))
                {
                    allNameLike = false;
                    break;
                }
            }
            if (anyName && allNameLike) return true;
        }
        return false;
    }

    // Suffixes that license a name segment (南アルプス市); without one it is a shred (ブリタニカ → ブリ+タニカ).
    private static readonly HashSet<string> NameContextSuffixes =
    [
        .. Grammar.TransitionRuleSets.HonorificSuffixes,
        "さま", "君", "どの", "先生", "先輩", "嬢", "卿",
        "市", "町", "村", "区", "郡", "県", "府", "都", "駅", "山", "川", "島", "湖", "港", "橋",
        "城", "寺", "神社", "線", "地方", "出身", "産", "語",
    ];

    private static bool HasNameSegmentOutsidePersonContext(
        SpanPath path, UncertainSpan span, SentenceInfo sentence, Dictionary<int, JmDictWordMeta> wordMeta)
    {
        // Re-splitting a name-only span into names is the intended outcome, whatever the context.
        if (span.NameOnly) return false;

        var word = sentence.Words[span.WordIndex].word;
        if (word.IsPersonNameContext) return false;

        // A katakana particle marks a styled sentence (ボクノナマエハ) where name-like fragments are fine.
        foreach (var s in path.Segments)
            if (s.Length == 1 && ResegmentationScorer.IsKatakanaParticleChar(span.Text[s.StartChar]))
                return false;

        for (int j = 0; j < path.Segments.Count; j++)
        {
            var s = path.Segments[j];
            bool anyName = false, allName = true;
            foreach (var id in s.WordIds)
            {
                if (!wordMeta.TryGetValue(id, out var meta)) continue;
                if (meta.IsTrueName) anyName = true;
                else { allName = false; break; }
            }

            if (!anyName || !allName) continue;

            // The following surface is the next segment, or the next sentence token for a span-final one.
            string? following = j + 1 < path.Segments.Count
                ? span.Text.Substring(path.Segments[j + 1].StartChar, path.Segments[j + 1].Length)
                : span.WordIndex + 1 < sentence.Words.Count
                    ? sentence.Words[span.WordIndex + 1].word.Text
                    : null;
            if (following == null || !NameContextSuffixes.Contains(following))
                return true;
        }

        return false;
    }

    // A bare する-conjugation tail only matches noun homographs (キャッキャウフフした must not shed 舌).
    private static bool HasSuruConjugationTail(SpanPath path, string text)
    {
        var last = path.Segments[^1];
        return text.Substring(last.StartChar, last.Length) is "し" or "した" or "して";
    }

    // A katakana-spelled conjugated word is one word (オカシクナイ → おかしい), never fragments like オカ|シク.
    private static bool IsKatakanaConjugatedWordSpelling(string text,
        Dictionary<string, List<int>> lookups, Dictionary<int, JmDictWordMeta> wordMeta)
    {
        if (text.Length < 3 || !JapaneseTextHelper.IsAllKatakana(text)) return false;

        string hira = KanaConverter.ToHiragana(text);
        foreach (var f in Deconjugator.Instance.Deconjugate(hira))
        {
            if (f.Process.Length == 0 || f.Text.Length < 2 || f.Text == hira) continue;
            if (!lookups.TryGetValue(f.Text, out var ids)) continue;
            foreach (var id in ids)
                if (wordMeta.TryGetValue(id, out var meta)
                    && meta.Pos.Any(p => p is PartOfSpeech.Verb or PartOfSpeech.IAdjective))
                    return true;
        }

        return false;
    }

    // A repeated kana segment is one mimetic (ずりずり), except repeated interjections (はいはいはい) which stay split.
    private static bool IsReduplicatedKanaSplit(SpanPath path, string text,
        Dictionary<int, JmDictWordMeta> wordMeta)
    {
        if (path.Segments.Count < 2) return false;

        var first = text.Substring(path.Segments[0].StartChar, path.Segments[0].Length);
        if (first.Length == 0 || !first.All(IsKana)) return false;

        foreach (var s in path.Segments.Skip(1))
            if (text.Substring(s.StartChar, s.Length) != first)
                return false;

        foreach (var id in path.Segments[0].WordIds)
            if (wordMeta.TryGetValue(id, out var meta) && meta.GetPrimaryPos() == PartOfSpeech.Interjection)
                return false;

        return true;
    }

    // Frequent single kana (リ/ン) shred OOV names (ゴブリン → ゴ+ブ+リ+ン); leading お/ご and katakana particles (オマエガ) pass.
    private static bool HasBadSingleKana(SpanPath path, string text)
    {
        foreach (var s in path.Segments)
        {
            if (s.Length != 1 || !IsKana(text[s.StartChar]))
                continue;
            if (s.StartChar == 0 && text[s.StartChar] is 'お' or 'ご')
                continue;
            if (ResegmentationScorer.IsKatakanaParticleChar(text[s.StartChar]))
                continue;
            return true;
        }
        return false;
    }

    private static bool IsKana(char c) => JapaneseTextHelper.IsKana(c);

    private static bool IsHiragana(char c) => JapaneseTextHelper.IsHiragana(c);

    private const int CompoundTailVerbRankAdvantage = 2;

    // Segments born here skip ApplyContextPins; ナシ means 無し, but the pear 梨 would win the rank tiebreak.
    private static readonly Dictionary<string, int> SegmentSurfacePins = new() { ["ナシ"] = 1529560, ["オラ"] = 2080360 };

    private static void ReplaceSpan(SentenceInfo sentence, UncertainSpan span, SpanPath path,
        Dictionary<string, List<int>> lookups, Dictionary<int, int> frequencyRanks, Dictionary<int, JmDictWordMeta> wordMeta,
        string source, int freqScore, int posScore, ParserDiagnostics? diagnostics)
    {
        if (path.Segments.Count == 0
            || path.Segments[0].StartChar != 0
            || path.Segments.Any(s => s.WordIds == null || s.WordIds.Count == 0))
            return;

        int RankOf(int id) => frequencyRanks.TryGetValue(id, out var r) ? r : int.MaxValue;

        var replacements = path.Segments.Select((seg, segIdx) =>
        {
            var text = span.Text.Substring(seg.StartChar, seg.Length);
            bool surfacePinned = SegmentSurfacePins.TryGetValue(text, out var pinnedId);
            int? bestWordId = surfacePinned
                ? pinnedId
                : seg.WordIds
                    .OrderBy(id => frequencyRanks.TryGetValue(id, out int r) ? r : int.MaxValue)
                    .Cast<int?>()
                    .FirstOrDefault();

            var pos = PartOfSpeech.Noun;
            if (bestWordId.HasValue && wordMeta.TryGetValue(bestWordId.Value, out var meta))
                pos = meta.GetPrimaryPos();

            var replacement = new WordInfo
            {
                Text                       = text,
                DictionaryForm             = text,
                NormalizedForm             = text,
                PartOfSpeech               = pos,
                Reading                    = text.All(IsKana) ? KanaConverter.ToHiragana(text) : string.Empty,
                PreMatchedWordId           = bestWordId,
                PreMatchedCandidateWordIds = surfacePinned && bestWordId.HasValue
                    ? [bestWordId.Value]
                    : seg.WordIds,
            };

            // An unattested compound's tail is a nominalised verb (逆回し → 回す) unless the noun is nearly as frequent (越し).
            if (segIdx > 0 && !surfacePinned
                && VerbStemLookup.Find(text, replacement.Reading, lookups, wordMeta, frequencyRanks) is { } verb
                && (long)RankOf(verb.WordId) * CompoundTailVerbRankAdvantage < seg.WordIds.Min(RankOf))
                VerbStemLookup.Pin(replacement, verb);

            return (replacement, span.Position + seg.StartChar, seg.Length);
        }).ToList();

        diagnostics?.LogParserEvent(
            source, "split",
            [span.Text],
            replacements.Select(r => $"{r.Item1.Text} (wordId={r.Item1.PreMatchedWordId})").ToArray(),
            $"freqScore={freqScore}, posScore={posScore}");

        sentence.Words.RemoveAt(span.WordIndex);
        sentence.Words.InsertRange(span.WordIndex, replacements);
    }
}
