using Jiten.Core;
using Jiten.Core.Data;

namespace Jiten.Parser.Resegmentation;

internal static class ResegmentationScorer
{
    private const int MaxSpanLength      = 14;
    private const int MaxEdgeLength      = 10;
    private const int MaxEdgesPerStart   = 10;
    private const int BeamWidth          = 16;

    public static List<SpanTokenCandidate> BuildEdges(
        string spanText,
        int startPos,
        Dictionary<string, List<int>> lookups)
    {
        var result = new List<SpanTokenCandidate>(MaxEdgesPerStart);
        int maxLen = Math.Min(MaxEdgeLength, spanText.Length - startPos);

        bool debug = spanText.Contains("ゴブリン") || spanText.Contains("ファルマ");

        for (int len = maxLen; len >= 1 && result.Count < MaxEdgesPerStart; len--)
        {
            var slice = spanText.Substring(startPos, len);
            List<int>? wordIds = null;

            if (lookups.TryGetValue(slice, out var direct) && direct.Count > 0)
            {
                wordIds = direct;
            }
            else
            {
                try
                {
                    var hira = KanaConverter.ToNormalizedHiragana(slice);
                    if (debug) Console.Error.WriteLine($"  [BuildEdges] pos={startPos} len={len} slice={slice} hira={hira} hiraMatch={lookups.ContainsKey(hira)}");
                    if (hira != slice && lookups.TryGetValue(hira, out var hiraIds) && hiraIds.Count > 0)
                        wordIds = hiraIds;

                }
                catch { }
            }

            if (wordIds != null)
            {
                if (debug) Console.Error.WriteLine($"  [BuildEdges] pos={startPos} len={len} MATCH slice={slice} ids=[{string.Join(",", wordIds.Take(3))}]");
                result.Add(new SpanTokenCandidate(startPos, len, wordIds));
            }
        }

        return result;
    }

    public static SpanPath? FindBestPath(
        string spanText,
        Dictionary<string, List<int>> lookups,
        Dictionary<int, int>? frequencyRanks = null,
        int beamWidth = BeamWidth)
    {
        if (spanText.Length == 0 || spanText.Length > MaxSpanLength)
            return null;

        // beamByPos[pos] = list of (segmentCount, lastLength, partialScore, segments)
        // partialScore = sum of per-segment frequency bonuses minus 15 per segment (matches ScorePath's additive terms)
        var beamByPos = new Dictionary<int, List<(int segCount, int lastLen, int partialScore, List<SpanTokenCandidate> segs)>>();
        beamByPos[0] = [(0, 0, 0, [])];

        for (int pos = 0; pos < spanText.Length; pos++)
        {
            if (!beamByPos.TryGetValue(pos, out var states) || states.Count == 0)
                continue;

            var edges = BuildEdges(spanText, pos, lookups);
            if (edges.Count == 0)
                continue;

            foreach (var state in states)
            {
                foreach (var edge in edges)
                {
                    int nextPos   = pos + edge.Length;
                    int nextCount = state.segCount + 1;

                    int edgeFreqBonus = 0;
                    if (frequencyRanks != null)
                    {
                        int bestRank = int.MaxValue;
                        foreach (int wordId in edge.WordIds)
                        {
                            if (frequencyRanks.TryGetValue(wordId, out int rank) && rank < bestRank)
                                bestRank = rank;
                        }
                        edgeFreqBonus = bestRank switch
                        {
                            <= 5000  => 30,
                            <= 15000 => 15,
                            <= 30000 => 5,
                            _        => 0
                        };
                    }

                    int edgeLengthBonus = edge.Length switch { >= 5 => 40, >= 4 => 25, >= 3 => 10, _ => 0 };
                    int nextPartial = state.partialScore + edgeFreqBonus + edgeLengthBonus - 15;

                    if (!beamByPos.TryGetValue(nextPos, out var nextStates))
                    {
                        nextStates = new List<(int, int, int, List<SpanTokenCandidate>)>(beamWidth);
                        beamByPos[nextPos] = nextStates;
                    }

                    var newSegs = new List<SpanTokenCandidate>(state.segs) { edge };
                    nextStates.Add((nextCount, edge.Length, nextPartial, newSegs));
                }
            }

            // Prune each bucket to beamWidth
            foreach (var (p, bucket) in beamByPos)
            {
                if (bucket.Count > beamWidth)
                {
                    if (frequencyRanks != null)
                    {
                        // Higher partial score first (encodes both frequency quality and fewer-segments penalty)
                        bucket.Sort((a, b) => b.partialScore.CompareTo(a.partialScore));
                    }
                    else
                    {
                        bucket.Sort((a, b) =>
                        {
                            int c = a.segCount.CompareTo(b.segCount);
                            return c != 0 ? c : b.lastLen.CompareTo(a.lastLen);
                        });
                    }
                    bucket.RemoveRange(beamWidth, bucket.Count - beamWidth);
                }
            }
        }

        if (!beamByPos.TryGetValue(spanText.Length, out var completeStates) || completeStates.Count == 0)
            return null;

        // Prefer paths that:
        // 1. don't exceed half the span length in segment count (too fragmented)
        // 2. don't have single-char kana in non-terminal positions (likely wrong word boundary)
        //    Exception: a single-char katakana at the LAST position is allowed (common particles like ガ/ニ/ヲ)
        int maxSegments = (spanText.Length + 1) / 2;
        var validStates = completeStates
            .Where(s => s.segCount <= maxSegments && !HasNonTerminalSingleCharKana(s.segs, spanText))
            .ToList();
        if (validStates.Count == 0)
            validStates = completeStates.Where(s => s.segCount <= maxSegments).ToList();
        if (validStates.Count == 0)
            validStates = completeStates;

        if (frequencyRanks != null)
        {
            // Use full ScorePath for final selection — adds structural bonuses not tracked in partial score.
            return validStates
                .Select(s => new SpanPath(s.segs))
                .MaxBy(p => ScorePath(p, frequencyRanks, spanText));
        }

        validStates.Sort((a, b) =>
        {
            int c = a.segCount.CompareTo(b.segCount);
            return c != 0 ? c : b.lastLen.CompareTo(a.lastLen);
        });

        return new SpanPath(validStates[0].segs);
    }

    internal static int ScorePath(SpanPath path, Dictionary<int, int> frequencyRanks, string? spanText = null)
    {
        int score = 0;
        int totalLength = 0;

        score -= 15 * path.Segments.Count;

        bool noWeakSingleCharSegments = true;
        foreach (var seg in path.Segments)
        {
            totalLength += seg.Length;
            if (seg.Length < 2)
            {
                if (spanText == null || JapaneseTextHelper.IsKana(spanText[seg.StartChar]))
                {
                    if (!(seg.StartChar == 0 && spanText != null && spanText[seg.StartChar] is 'お' or 'ご'))
                        noWeakSingleCharSegments = false;
                }
            }

            int bestRank = int.MaxValue;
            foreach (int wordId in seg.WordIds)
            {
                if (frequencyRanks.TryGetValue(wordId, out int rank) && rank < bestRank)
                    bestRank = rank;
            }

            score += bestRank switch
            {
                <= 5000  => 30,
                <= 15000 => 15,
                <= 30000 => 5,
                _        => 0
            };

            score += seg.Length switch { >= 5 => 40, >= 4 => 25, >= 3 => 10, _ => 0 };
        }

        if (noWeakSingleCharSegments)
            score += 50;

        if (path.Segments.Count > 0 && totalLength / path.Segments.Count >= 3)
            score += 20;

        if (spanText != null && path.Segments.Count >= 2
            && path.Segments[0] is { StartChar: 0, Length: 1 }
            && spanText[0] is 'お' or 'ご')
            score += 35;

        return score;
    }

    internal static int ScorePosTransitions(
        SpanPath path,
        Dictionary<int, PartOfSpeech> wordPosByWordId,
        PartOfSpeech? prevNeighborPos,
        PartOfSpeech? nextNeighborPos,
        Dictionary<int, int>? frequencyRanks = null)
    {
        int score = 0;

        var firstPos = ResolveSegmentPos(path.Segments[0], wordPosByWordId, frequencyRanks);
        var lastPos  = ResolveSegmentPos(path.Segments[^1], wordPosByWordId, frequencyRanks);

        if (lastPos.HasValue && IsNounLike(lastPos.Value) && nextNeighborPos == PartOfSpeech.Particle)
            score += 20;

        if (lastPos.HasValue && IsPredicateHost(lastPos.Value) && nextNeighborPos == PartOfSpeech.Auxiliary)
            score += 15;

        if (prevNeighborPos == PartOfSpeech.Numeral && firstPos == PartOfSpeech.Counter)
            score += 30;

        if (firstPos == PartOfSpeech.Counter && prevNeighborPos.HasValue && !IsNumericLike(prevNeighborPos.Value))
            score -= 15;

        if (prevNeighborPos == PartOfSpeech.Particle && firstPos.HasValue && IsNounLike(firstPos.Value))
            score += 10;

        if (prevNeighborPos == PartOfSpeech.Particle && firstPos.HasValue && !IsNounLike(firstPos.Value))
            score -= 15;

        bool allNounLike = path.Segments.All(s => ResolveSegmentPos(s, wordPosByWordId, frequencyRanks) is { } p && IsNounLike(p));
        if (allNounLike && path.Segments.Count > 1)
            score += 10;

        for (int i = 0; i < path.Segments.Count - 1; i++)
        {
            var a = ResolveSegmentPos(path.Segments[i],     wordPosByWordId, frequencyRanks);
            var b = ResolveSegmentPos(path.Segments[i + 1], wordPosByWordId, frequencyRanks);
            if (a == PartOfSpeech.Particle && b == PartOfSpeech.Particle)
                score -= 20;
        }

        return score;
    }

    private static PartOfSpeech? ResolveSegmentPos(
        SpanTokenCandidate seg,
        Dictionary<int, PartOfSpeech> wordPosByWordId,
        Dictionary<int, int>? frequencyRanks)
    {
        int bestRank = int.MaxValue;
        PartOfSpeech? bestPos = null;

        foreach (int id in seg.WordIds)
        {
            if (!wordPosByWordId.TryGetValue(id, out var pos)) continue;
            int rank = frequencyRanks != null && frequencyRanks.TryGetValue(id, out int r) ? r : int.MaxValue - 1;
            if (rank < bestRank)
            {
                bestRank = rank;
                bestPos  = pos;
            }
        }

        return bestPos;
    }

    private static bool IsNounLike(PartOfSpeech pos) =>
        pos is PartOfSpeech.Noun or PartOfSpeech.CommonNoun or PartOfSpeech.NaAdjective
            or PartOfSpeech.NominalAdjective or PartOfSpeech.Pronoun or PartOfSpeech.Expression;

    private static bool IsPredicateHost(PartOfSpeech pos) =>
        pos is PartOfSpeech.Verb or PartOfSpeech.IAdjective or PartOfSpeech.NaAdjective or PartOfSpeech.Auxiliary;

    private static bool IsNumericLike(PartOfSpeech pos) =>
        pos is PartOfSpeech.Numeral or PartOfSpeech.Prefix
            or PartOfSpeech.Noun or PartOfSpeech.CommonNoun
            or PartOfSpeech.Pronoun or PartOfSpeech.Name;

    private static bool HasNonTerminalSingleCharKana(List<SpanTokenCandidate> segs, string spanText)
    {
        for (int i = 0; i < segs.Count - 1; i++)
        {
            var seg = segs[i];
            if (seg.Length != 1) continue;
            char c = spanText[seg.StartChar];
            if (seg.StartChar == 0 && c is '\u304A' or '\u3054') continue;
            if (c is >= '\u3041' and <= '\u3096' || c is >= '\u30A1' and <= '\u30F6')
                return true;
        }
        return false;
    }
}
