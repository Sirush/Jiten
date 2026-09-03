using System.Text;

namespace Jiten.Parser.Diagnostics;

/// Process-wide event counters for the parse hot path. Increments are lock-free and cheap enough
/// to stay on in production; the benchmark command prints and resets them.
public static class ParserCounters
{
    public static long AdjTokens;
    public static long AdjHighConfidenceSkips;
    public static long AdjSoftRuleSkips;
    public static long AdjFirstPassCandidates;
    public static long AdjRederived;
    public static long AdjCandidatesBuilt;
    public static long AdjTokensScored;
    public static long AdjMemoHits;
    public static long AdjTokensChanged;
    public static long EnumerateFormsCalls;
    public static long JmDictInProcessHits;
    public static long JmDictInProcessMisses;
    public static long JmDictRedisMisses;
    public static long JmDictDbRows;
    public static long DeckWordInProcessHits;
    public static long DeckWordCacheHits;
    public static long DeckWordCacheMisses;

    public static void Add(ref long counter, long value) => Interlocked.Add(ref counter, value);

    public enum Section
    {
        AdjPass1,
        AdjCollectIds,
        AdjWordFetch,
        AdjPass2,
        AdjBuildCandidates,
        AdjContext,
        AdjScoreCandidates,
        AdjSelect,
    }

    /// Off by default: the per-section timestamps cost a few ms per document and only the benchmark reads them.
    public static bool SectionTiming;
    private static readonly long[] SectionTicks = new long[Enum.GetValues<Section>().Length];

    public static void AddSection(Section section, long startTimestamp) =>
        Interlocked.Add(ref SectionTicks[(int)section], System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp);

    public static void Lap(Section section, ref long mark)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        Interlocked.Add(ref SectionTicks[(int)section], now - mark);
        mark = now;
    }

    public static string Dump()
    {
        var sb = new StringBuilder();
        void Line(string name, long value) => sb.Append($"    {name,-28} {value,12:N0}\n");
        Line("AdjTokens", AdjTokens);
        Line("AdjHighConfidenceSkips", AdjHighConfidenceSkips);
        Line("AdjSoftRuleSkips", AdjSoftRuleSkips);
        Line("AdjFirstPassCandidates", AdjFirstPassCandidates);
        Line("AdjRederived", AdjRederived);
        Line("AdjCandidatesBuilt", AdjCandidatesBuilt);
        Line("AdjTokensScored", AdjTokensScored);
        Line("AdjMemoHits", AdjMemoHits);
        Line("AdjTokensChanged", AdjTokensChanged);
        Line("EnumerateFormsCalls", EnumerateFormsCalls);
        Line("JmDictInProcessHits", JmDictInProcessHits);
        Line("JmDictInProcessMisses", JmDictInProcessMisses);
        Line("JmDictRedisMisses", JmDictRedisMisses);
        Line("JmDictDbRows", JmDictDbRows);
        Line("DeckWordInProcessHits", DeckWordInProcessHits);
        Line("DeckWordCacheHits", DeckWordCacheHits);
        Line("DeckWordCacheMisses", DeckWordCacheMisses);
        if (SectionTiming)
        {
            sb.Append("  Adjacent scoring sections (ms):\n");
            foreach (var section in Enum.GetValues<Section>())
                sb.Append($"    {section,-28} {SectionTicks[(int)section] * 1000.0 / System.Diagnostics.Stopwatch.Frequency,12:N1}\n");
        }
        return sb.ToString();
    }

    public static void Reset()
    {
        AdjTokens = AdjHighConfidenceSkips = AdjSoftRuleSkips = AdjFirstPassCandidates = AdjRederived =
            AdjCandidatesBuilt = AdjTokensScored = AdjMemoHits = AdjTokensChanged = EnumerateFormsCalls =
                JmDictInProcessHits = JmDictInProcessMisses = JmDictRedisMisses = JmDictDbRows =
                    DeckWordInProcessHits = DeckWordCacheHits = DeckWordCacheMisses = 0;
        Array.Clear(SectionTicks);
    }
}
