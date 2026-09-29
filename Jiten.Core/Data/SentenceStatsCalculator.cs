namespace Jiten.Core.Data;

/// <summary>Consecutive parts (episodes, volumes) grouped together; for a single-text deck, an equal slice of its sentences with FirstPart and LastPart both 0.</summary>
public sealed record SentenceSegmentStats(int Index, int FirstPart, int LastPart, int Total, int Readable, int OneUnknown, int TwoUnknown);

/// <summary>Unlocked counts sentences that become fully readable when this word is learned; ReadableAfter is the running total.</summary>
public sealed record SentenceLearnStep(int WordKey, int Unlocked, int ReadableAfter);

/// <summary>Readable sentence counts after learning <see cref="Words"/> unknown words, picked greedily or by occurrence count.</summary>
public sealed record SentenceProjectionPoint(int Words, int Greedy, int ByFrequency);

/// <summary>Greedy-order words needed for <see cref="Percent"/>% of sentences to be readable; null if not reached within the step cap.</summary>
public sealed record SentenceMilestone(int Percent, int? Words);

public sealed class SentenceStats
{
    public int Total { get; init; }

    /// <summary>Sentence counts indexed by distinct unknown words; index 3 means 3 or more.</summary>
    public int[] ByUnknown { get; init; } = new int[4];

    public int UnknownWords { get; init; }
    public List<SentenceSegmentStats> Segments { get; init; } = [];
    public List<SentenceLearnStep> LearnNext { get; init; } = [];
    public List<SentenceProjectionPoint> Projection { get; init; } = [];
    public List<SentenceMilestone> Milestones { get; init; } = [];
}

public static class SentenceStatsCalculator
{
    private static readonly int[] Checkpoints = [0, 5, 10, 20, 30, 50, 75, 100, 150, 200, 300, 400, 500, 750, 1000, 1500, 2000];
    private static readonly int[] MilestonePercents = [50, 60, 70, 80, 90];

    /// <param name="parts">In reading order: one per child deck, or the deck itself when it has none.</param>
    /// <param name="textSlices">Segment count when there is only one part.</param>
    /// <param name="maxSteps">Words simulated for the projection and milestones.</param>
    public static SentenceStats Compute(IReadOnlyList<SentenceProfile> parts, Func<int, bool> isKnown,
                                        int maxSegments = 60, int textSlices = 10, int learnNextCount = 100, int maxSteps = 2000)
    {
        var gidOf = new Dictionary<int, int>();
        var keys = new List<int>();
        var partMaps = new int[parts.Count][];
        int total = 0, idCount = 0;
        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            var map = new int[part.Keys.Length];
            for (int i = 0; i < map.Length; i++)
            {
                if (!gidOf.TryGetValue(part.Keys[i], out var gid))
                {
                    gid = keys.Count;
                    gidOf[part.Keys[i]] = gid;
                    keys.Add(part.Keys[i]);
                }

                map[i] = gid;
            }

            partMaps[p] = map;
            total += part.SentenceCount;
            idCount += part.Ids.Length;
        }

        var known = new bool[keys.Count];
        for (int g = 0; g < known.Length; g++)
            known[g] = isKnown(keys[g]);

        var starts = new int[total + 1];
        var gids = new int[idCount];
        var partOf = new int[total];
        var unknownCount = new int[total];
        int s = 0, pos = 0;
        for (int p = 0; p < parts.Count; p++)
        {
            var part = parts[p];
            var map = partMaps[p];
            for (int i = 0; i < part.SentenceCount; i++, s++)
            {
                starts[s] = pos;
                partOf[s] = p;
                foreach (var id in part.Sentence(i))
                {
                    var g = map[id];
                    gids[pos++] = g;
                    if (!known[g]) unknownCount[s]++;
                }
            }
        }

        starts[total] = pos;

        var byUnknown = new int[4];
        foreach (var count in unknownCount)
            byUnknown[Math.Min(count, 3)]++;

        var segments = BuildSegments(parts.Count, total, partOf, unknownCount, maxSegments, textSlices);

        var occurrences = new int[keys.Count];
        var unlocks = new int[keys.Count];
        for (s = 0; s < total; s++)
        {
            if (unknownCount[s] == 0) continue;
            for (int j = starts[s]; j < starts[s + 1]; j++)
            {
                var g = gids[j];
                if (known[g]) continue;
                occurrences[g]++;
                if (unknownCount[s] == 1) unlocks[g]++;
            }
        }

        var inverseStart = new int[keys.Count + 1];
        for (int g = 0; g < keys.Count; g++)
            inverseStart[g + 1] = inverseStart[g] + occurrences[g];
        var inverse = new int[inverseStart[keys.Count]];
        var fill = (int[])inverseStart.Clone();
        for (s = 0; s < total; s++)
        {
            if (unknownCount[s] == 0) continue;
            for (int j = starts[s]; j < starts[s + 1]; j++)
                if (!known[gids[j]])
                    inverse[fill[gids[j]]++] = s;
        }

        var unknownWords = new List<int>();
        for (int g = 0; g < keys.Count; g++)
            if (occurrences[g] > 0)
                unknownWords.Add(g);

        var learnNext = new List<SentenceLearnStep>();
        var greedy = RunGreedy(unknownWords, keys, known, gids, starts, inverseStart, inverse, unknownCount, unlocks, occurrences,
                               byUnknown[0], maxSteps, learnNextCount, learnNext);

        var frequencyOrder = unknownWords.OrderByDescending(g => occurrences[g]).ThenBy(g => keys[g]).ToList();
        var byFrequency = RunInOrder(frequencyOrder, inverseStart, inverse, unknownCount, byUnknown[0], maxSteps);

        var projection = new List<SentenceProjectionPoint>();
        int longest = Math.Max(greedy.Count, byFrequency.Count) - 1;
        foreach (var words in Checkpoints)
        {
            if (words > longest) break;
            projection.Add(new SentenceProjectionPoint(words, greedy[Math.Min(words, greedy.Count - 1)],
                                                       byFrequency[Math.Min(words, byFrequency.Count - 1)]));
        }

        if (longest > 0 && projection[^1].Words < longest)
            projection.Add(new SentenceProjectionPoint(longest, greedy[Math.Min(longest, greedy.Count - 1)],
                                                       byFrequency[Math.Min(longest, byFrequency.Count - 1)]));

        var milestones = MilestonePercents.Select(percent =>
        {
            var threshold = (int)Math.Ceiling(percent * total / 100.0);
            var step = total == 0 ? -1 : greedy.FindIndex(r => r >= threshold);
            return new SentenceMilestone(percent, step >= 0 ? step : null);
        }).ToList();

        return new SentenceStats
        {
            Total = total,
            ByUnknown = byUnknown,
            UnknownWords = unknownWords.Count,
            Segments = segments,
            LearnNext = learnNext,
            Projection = projection,
            Milestones = milestones
        };
    }

    private static List<SentenceSegmentStats> BuildSegments(int partCount, int total, int[] partOf, int[] unknownCount,
                                                            int maxSegments, int textSlices)
    {
        bool byPart = partCount > 1;
        int buckets = byPart ? Math.Min(partCount, maxSegments) : Math.Min(textSlices, total);
        if (buckets == 0) return [];

        var totals = new int[buckets];
        var readable = new int[buckets];
        var oneUnknown = new int[buckets];
        var twoUnknown = new int[buckets];
        for (int s = 0; s < total; s++)
        {
            int bucket = byPart ? (int)((long)partOf[s] * buckets / partCount) : (int)((long)s * buckets / total);
            totals[bucket]++;
            if (unknownCount[s] == 0) readable[bucket]++;
            else if (unknownCount[s] == 1) oneUnknown[bucket]++;
            else if (unknownCount[s] == 2) twoUnknown[bucket]++;
        }

        var segments = new List<SentenceSegmentStats>(buckets);
        for (int b = 0; b < buckets; b++)
        {
            int firstPart = byPart ? (int)Math.Ceiling((double)b * partCount / buckets) : 0;
            int lastPart = byPart ? (int)Math.Ceiling((double)(b + 1) * partCount / buckets) - 1 : 0;
            segments.Add(new SentenceSegmentStats(b, firstPart, lastPart, totals[b], readable[b], oneUnknown[b], twoUnknown[b]));
        }

        return segments;
    }

    /// <summary>Repeatedly learns the word that unlocks the most sentences, ties broken by occurrences; returned index 0 is the count before any word.</summary>
    private static List<int> RunGreedy(List<int> unknownWords, List<int> keys, bool[] known, int[] gids, int[] starts,
                                       int[] inverseStart, int[] inverse, int[] unknownCount, int[] unlocks, int[] occurrences,
                                       int readable, int maxSteps, int learnNextCount, List<SentenceLearnStep> learnNext)
    {
        var remaining = (int[])unknownCount.Clone();
        var currentUnlocks = (int[])unlocks.Clone();
        var learned = new bool[keys.Count];
        var queue = new PriorityQueue<int, (int Unlocks, int Occurrences, int Key)>(unknownWords.Count, PriorityComparer.Instance);
        foreach (var g in unknownWords)
            queue.Enqueue(g, (currentUnlocks[g], occurrences[g], keys[g]));

        var curve = new List<int> { readable };
        while (curve.Count <= maxSteps && queue.TryDequeue(out var word, out var priority))
        {
            // Lazy deletion: an outdated unlock count means a newer entry for this word is already queued.
            if (learned[word] || priority.Unlocks != currentUnlocks[word]) continue;

            learned[word] = true;
            int unlocked = 0;
            for (int i = inverseStart[word]; i < inverseStart[word + 1]; i++)
            {
                int sentence = inverse[i];
                int before = remaining[sentence]--;
                if (before == 1)
                {
                    readable++;
                    unlocked++;
                }
                else if (before == 2)
                {
                    for (int j = starts[sentence]; j < starts[sentence + 1]; j++)
                    {
                        var other = gids[j];
                        if (known[other] || learned[other]) continue;
                        currentUnlocks[other]++;
                        queue.Enqueue(other, (currentUnlocks[other], occurrences[other], keys[other]));
                        break;
                    }
                }
            }

            curve.Add(readable);
            if (learnNext.Count < learnNextCount)
                learnNext.Add(new SentenceLearnStep(keys[word], unlocked, readable));
        }

        return curve;
    }

    private static List<int> RunInOrder(List<int> order, int[] inverseStart, int[] inverse, int[] unknownCount, int readable, int maxSteps)
    {
        var remaining = (int[])unknownCount.Clone();
        var curve = new List<int> { readable };
        foreach (var word in order)
        {
            if (curve.Count > maxSteps) break;
            for (int i = inverseStart[word]; i < inverseStart[word + 1]; i++)
                if (--remaining[inverse[i]] == 0)
                    readable++;
            curve.Add(readable);
        }

        return curve;
    }

    private sealed class PriorityComparer : IComparer<(int Unlocks, int Occurrences, int Key)>
    {
        public static readonly PriorityComparer Instance = new();

        public int Compare((int Unlocks, int Occurrences, int Key) x, (int Unlocks, int Occurrences, int Key) y)
        {
            if (x.Unlocks != y.Unlocks) return y.Unlocks.CompareTo(x.Unlocks);
            if (x.Occurrences != y.Occurrences) return y.Occurrences.CompareTo(x.Occurrences);
            return x.Key.CompareTo(y.Key);
        }
    }
}
