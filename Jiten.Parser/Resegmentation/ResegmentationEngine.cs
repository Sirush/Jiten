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
            var spans = UncertaintyDetector.FindSpans(sentence, lookups, protectedSurfaces);
            if (spans.Count == 0) continue;

            foreach (var span in spans.OrderByDescending(s => s.WordIndex))
            {
                var word = sentence.Words[span.WordIndex].word;
                bool isCompoundNumeral = word.PartOfSpeechSection1 == PartOfSpeechSection.Numeral && word.Text.Length > 1;

                SpanPath? path;
                if (isCompoundNumeral)
                {
                    path = TrySplitCompoundNumeral(span.Text, lookups);
                    if (path == null)
                        continue;
                }
                else
                {
                    path = ResegmentationScorer.FindBestPath(span.Text, lookups, frequencyRanks);
                    var rejection = path switch
                    {
                        null => "no path found",
                        _ when !path.IsComplete(span.Text.Length) => "path incomplete",
                        _ when path.Segments.Count <= 1 => "single segment",
                        _ when path.Segments.Count > (span.Text.Length + 1) / 2 => "too fragmented",
                        _ when HasBadSingleKana(path, span.Text) => "single-kana segment",
                        _ when HasShortPureNameSegment(path, wordMeta) => "short pure-name segment",
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
            ReplaceSpan(sentence, span, path, frequencyRanks, wordMeta,
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

                var path = ResegmentationScorer.FindBestPath(word.Text, lookups, frequencyRanks);
                if (path == null || !path.IsComplete(word.Text.Length) || path.Segments.Count <= 1)
                    continue;
                if (path.Segments.Count > (word.Text.Length + 1) / 2)
                    continue;
                if (HasBadSingleKana(path, word.Text))
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
            ReplaceSpan(sentence, span, path, frequencyRanks, wordMeta,
                        "Resegmentation.LowConfidence", freqScore, posScore, diagnostics);
            anyApplied = true;
        }

        return anyApplied;
    }

    // Splits compound kanji numerals at the last place marker (十/百/千/万/億/兆).
    // E.g. 五十七 → 五十+七, 三十八 → 三十+八, 六十一 → 六十+一.
    private static SpanPath? TrySplitCompoundNumeral(string text, Dictionary<string, List<int>> lookups)
    {
        for (int i = text.Length - 1; i >= 1; i--)
        {
            if (text[i - 1] is not ('十' or '百' or '千' or '万' or '億' or '兆'))
                continue;

            var left = text[..i];
            var right = text[i..];

            if (!lookups.TryGetValue(left, out var leftIds) || leftIds.Count == 0)
                continue;
            if (!lookups.TryGetValue(right, out var rightIds) || rightIds.Count == 0)
                continue;

            return new SpanPath([
                new SpanTokenCandidate(0, left.Length, leftIds),
                new SpanTokenCandidate(i, right.Length, rightIds)
            ]);
        }

        return null;
    }

    // A short segment whose every candidate is a pure JMnedict name entry is not evidence of a
    // word boundary — it shreds OOV names into coincidental fragments (ファルマ → ファ+ルマ "Ruma").
    // Long pure-name segments stay allowed: fused name sequences legitimately resegment through
    // them (ラムシャーリー → ラム+シャーリー, 南アルプス市 → 南アルプス+市).
    private static bool HasShortPureNameSegment(SpanPath path, Dictionary<int, JmDictWordMeta> wordMeta)
    {
        foreach (var s in path.Segments)
        {
            if (s.Length > 2)
                continue;

            // Only actual name entries (name-fem, surname, place...) count as fragments;
            // JMnedict "unclass" entries cover legitimate slang like ダサ despite mapping to Name.
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

    // Single-kana segments are noise matches (high-frequency entries like 部/リ/ン win on
    // frequency score and shred OOV katakana names, e.g. ゴブリンスレイヤー → ゴ+ブ+リ+ン+スレイヤー).
    // Exceptions: honorific お/ご at the start, and katakana-styled particles anywhere
    // (オマエガ → オマエ+ガ, ナニガ悪イ → ナニ+ガ+悪イ).
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

    private static void ReplaceSpan(SentenceInfo sentence, UncertainSpan span, SpanPath path,
        Dictionary<int, int> frequencyRanks, Dictionary<int, JmDictWordMeta> wordMeta,
        string source, int freqScore, int posScore, ParserDiagnostics? diagnostics)
    {
        if (path.Segments.Count == 0
            || path.Segments[0].StartChar != 0
            || path.Segments.Any(s => s.WordIds == null || s.WordIds.Count == 0))
            return;

        var replacements = path.Segments.Select(seg =>
        {
            var text = span.Text.Substring(seg.StartChar, seg.Length);
            int? bestWordId = seg.WordIds
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
                PreMatchedCandidateWordIds = seg.WordIds,
            };
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
