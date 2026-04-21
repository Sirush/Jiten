using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Data.Redis;
using Jiten.Parser.Diagnostics;
using Jiten.Parser.Scoring;

namespace Jiten.Parser.Resegmentation;

internal static class ResegmentationEngine
{
    private const int MinAcceptScore = 25;
    private const int MinAcceptScoreConfidence = 50;

    public static async Task TryImproveUncertainSpans(
        List<SentenceInfo> sentences,
        Dictionary<string, List<int>> lookups,
        Dictionary<int, int> frequencyRanks,
        IJmDictCache jmDictCache,
        ParserDiagnostics? diagnostics = null)
    {
        // Phase 1: collect all valid (sentence, span, path) tuples — pure CPU, no I/O
        var pending = new List<(SentenceInfo sentence, UncertainSpan span, SpanPath path, PartOfSpeech? prevPos, PartOfSpeech? nextPos)>();
        var candidates = new DirectLookupCandidateProvider(lookups);

        foreach (var sentence in sentences)
        {
            var spans = UncertaintyDetector.FindSpans(sentence, lookups);
            if (spans.Count == 0) continue;

            foreach (var span in spans.OrderByDescending(s => s.WordIndex))
            {
                var path = ResegmentationScorer.FindBestPath(span.Text, candidates, frequencyRanks);
                if (path == null || !path.IsComplete(span.Text.Length) || path.Segments.Count <= 1)
                    continue;
                if (path.Segments.Count > (span.Text.Length + 1) / 2)
                    continue;
                if (path.Segments.Any(s => s.Length == 1 && IsHiragana(span.Text[s.StartChar])))
                    continue;
                // Pre-filter: paths with negative freqScore have no chance even with maximum posScore bonus.
                // Paths with freqScore between 0 and MinAcceptScore may still be saved by POS context.
                if (ResegmentationScorer.ScorePath(path, frequencyRanks, span.Text) < 0)
                    continue;

                var prevPos = span.WordIndex > 0 ? sentence.Words[span.WordIndex - 1].word.PartOfSpeech : (PartOfSpeech?)null;
                var nextPos = span.WordIndex < sentence.Words.Count - 1 ? sentence.Words[span.WordIndex + 1].word.PartOfSpeech : (PartOfSpeech?)null;

                pending.Add((sentence, span, path, prevPos, nextPos));
            }
        }

        if (pending.Count == 0) return;

        // Phase 2: one batch fetch for all word IDs across all sentences
        var allWordIds = pending.SelectMany(p => p.path.Segments.SelectMany(s => s.WordIds)).Distinct().ToList();
        var wordCache = await jmDictCache.GetWordsAsync(allWordIds);

        var wordPosByWordId = wordCache.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.CachedPOS
                       .FirstOrDefault(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown), PartOfSpeech.Noun));

        // Phase 3: apply replacements (already in reverse word-index order per sentence from the loop above)
        foreach (var (sentence, span, path, prevPos, nextPos) in pending)
        {
            var freqScore = ResegmentationScorer.ScorePath(path, frequencyRanks, span.Text);
            var posScore  = ResegmentationScorer.ScorePosTransitions(path, wordPosByWordId, prevPos, nextPos, frequencyRanks);
            bool accepted = freqScore + posScore >= MinAcceptScore;
            diagnostics?.LogResegmentationPath(span.Text, path.Segments.Count, path.GapChars,
                path.GapCost, freqScore + posScore, accepted, "ImproveUncertainSpans");
            if (!accepted)
                continue;
            ReplaceSpan(sentence, span, path, frequencyRanks, wordCache);
        }
    }

    public static async Task<bool> TryResegmentLowConfidenceTokens(
        List<SentenceInfo> sentences,
        Dictionary<string, List<int>> lookups,
        Dictionary<int, int> frequencyRanks,
        Dictionary<(int sentenceIndex, int wordIndex), int?> marginMap,
        IJmDictCache jmDictCache,
        ParserDiagnostics? diagnostics = null)
    {
        // Phase 1: collect all valid replacements — pure CPU, no I/O
        var pending = new List<(SentenceInfo sentence, UncertainSpan span, SpanPath path, PartOfSpeech? prevPos, PartOfSpeech? nextPos)>();
        var candidates = new DirectLookupCandidateProvider(lookups);

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
                if (!marginMap.TryGetValue((si, wi), out var margin) || margin == null || margin >= ScoringPolicy.LowConfidenceThreshold)
                    continue;

                var path = ResegmentationScorer.FindBestPath(word.Text, candidates, frequencyRanks);
                if (path == null || !path.IsComplete(word.Text.Length) || path.Segments.Count <= 1)
                    continue;
                if (path.Segments.Count > (word.Text.Length + 1) / 2)
                    continue;
                if (path.Segments.Any(s => s.Length == 1 && IsHiragana(word.Text[s.StartChar])))
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

        // Phase 2: one batch fetch for all word IDs across all sentences
        var allWordIds = pending.SelectMany(p => p.path.Segments.SelectMany(s => s.WordIds)).Distinct().ToList();
        var wordCache = await jmDictCache.GetWordsAsync(allWordIds);

        var wordPosByWordId = wordCache.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.CachedPOS
                       .FirstOrDefault(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown), PartOfSpeech.Noun));

        // Phase 3: apply replacements (already in reverse word-index order per sentence from the loop above)
        bool anyApplied = false;
        foreach (var (sentence, span, path, prevPos, nextPos) in pending)
        {
            var freqScore = ResegmentationScorer.ScorePath(path, frequencyRanks, span.Text);
            var posScore  = ResegmentationScorer.ScorePosTransitions(path, wordPosByWordId, prevPos, nextPos, frequencyRanks);
            bool accepted = freqScore + posScore >= MinAcceptScoreConfidence;
            diagnostics?.LogResegmentationPath(span.Text, path.Segments.Count, path.GapChars,
                path.GapCost, freqScore + posScore, accepted, "LowConfidenceTokens");
            if (!accepted)
                continue;
            ReplaceSpan(sentence, span, path, frequencyRanks, wordCache);
            anyApplied = true;
        }

        return anyApplied;
    }

    private static bool IsKana(char c) => JapaneseTextHelper.IsKana(c);

    private static bool IsHiragana(char c) => JapaneseTextHelper.IsHiragana(c);

    private static void ReplaceSpan(SentenceInfo sentence, UncertainSpan span, SpanPath path,
        Dictionary<int, int> frequencyRanks, Dictionary<int, JmDictWord> wordCache)
    {
        // Refuse to apply a resegmentation where any segment has no WordId candidates
        // or where the path doesn't cover the span from position 0. Without these guards
        // user-visible text can silently disappear (e.g. ファルマ → ルマ with ファ dropping
        // because IsComplete only checks end coverage, not start coverage).
        // Also guards against gap segments (WordIds empty) emitted by FindBestPath's
        // uncovered-char fallback — those are scored for path selection but should never
        // replace user-visible tokens here.
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
            if (bestWordId.HasValue && wordCache.TryGetValue(bestWordId.Value, out var jmWord))
            {
                var posList = jmWord.CachedPOS;
                pos = posList.FirstOrDefault(p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown), PartOfSpeech.Noun);
            }

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

        sentence.Words.RemoveAt(span.WordIndex);
        sentence.Words.InsertRange(span.WordIndex, replacements);
    }
}
