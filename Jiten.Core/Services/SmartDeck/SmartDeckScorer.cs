namespace Jiten.Core.Services.SmartDeck;

public readonly record struct SmartDeckWordOccurrence(int WordId, byte ReadingIndex, int Occurrences);

public sealed record SmartDeckPart(int DeckId, double Weight, IReadOnlyList<SmartDeckWordOccurrence> Words,
                                   int TargetPercentage = SmartDeckConstants.MaxTargetPercentage);

public sealed record SmartDeckTitleInput(int ParentDeckId, double TitleWeight, IReadOnlyList<SmartDeckPart> Parts);

public readonly record struct SmartDeckScoredWord(long Key, double Score)
{
    public int WordId => (int)(Key >> 8);
    public byte ReadingIndex => (byte)(Key & 0xFF);
}

public static class SmartDeckScorer
{
    public static long EncodeKey(int wordId, byte readingIndex) => ((long)wordId << 8) | readingIndex;

    /// <summary>score = sum over titles of w_title * sum over parts of w_part * log2(1 + occ). Ties break on frequency rank then WordId. Each part only contributes the words inside its own coverage target.</summary>
    public static List<SmartDeckScoredWord> Score(
        IReadOnlyList<SmartDeckTitleInput> titles,
        Func<long, bool> isExcluded,
        Func<long, int> frequencyRank,
        int cap,
        Func<long, bool>? hasCard = null)
    {
        hasCard ??= _ => false;
        var scores = new Dictionary<long, double>(capacity: 65_536);

        foreach (var title in titles)
        {
            if (title.TitleWeight <= 0) continue;

            foreach (var part in title.Parts)
            {
                var factor = title.TitleWeight * part.Weight;
                if (factor <= 0) continue;

                foreach (var word in CoverageSlice(part.Words, isExcluded, part.TargetPercentage, hasCard))
                {
                    var key = EncodeKey(word.WordId, word.ReadingIndex);
                    var contribution = factor * Math.Log2(1 + word.Occurrences);
                    scores[key] = scores.TryGetValue(key, out var existing) ? existing + contribution : contribution;
                }
            }
        }

        var ranked = scores.Select(kv => new SmartDeckScoredWord(kv.Key, kv.Value)).ToList();
        ranked.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            var byRank = frequencyRank(a.Key).CompareTo(frequencyRank(b.Key));
            return byRank != 0 ? byRank : a.Key.CompareTo(b.Key);
        });

        if (ranked.Count > cap) ranked.RemoveRange(cap, ranked.Count - cap);
        return ranked;
    }

    public static IEnumerable<SmartDeckWordOccurrence> CoverageSlice(
        IReadOnlyList<SmartDeckWordOccurrence> words, Func<long, bool> isExcluded, int targetPercentage, Func<long, bool>? hasCard = null)
    {
        hasCard ??= _ => false;
        long total = 0, covered = 0;
        var unknown = new List<SmartDeckWordOccurrence>(words.Count);
        foreach (var word in words)
        {
            total += word.Occurrences;
            var key = EncodeKey(word.WordId, word.ReadingIndex);
            if (hasCard(key))
            {
                covered += word.Occurrences;
                yield return word;
            }
            else if (isExcluded(key)) covered += word.Occurrences;
            else unknown.Add(word);
        }

        if (total == 0) yield break;

        if (targetPercentage >= SmartDeckConstants.MaxTargetPercentage)
        {
            foreach (var word in unknown) yield return word;
            yield break;
        }

        unknown.Sort((a, b) => b.Occurrences.CompareTo(a.Occurrences));
        var goal = total * targetPercentage;
        foreach (var word in unknown)
        {
            if (covered * 100 >= goal) yield break;
            covered += word.Occurrences;
            yield return word;
        }
    }
}