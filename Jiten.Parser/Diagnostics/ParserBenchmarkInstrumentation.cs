using System.Diagnostics;

namespace Jiten.Parser.Diagnostics;

/// <summary>
/// Lightweight stage-timing instrumentation for parser benchmarks.
/// Disabled by default: when <see cref="Enabled"/> is false, all probe calls are
/// near-zero overhead (a single boolean branch).
///
/// Not thread-safe: intended for single-threaded benchmark runs. Callers should
/// <see cref="Reset"/> before each parse, then <see cref="Capture"/> after.
/// </summary>
public static class ParserBenchmarkInstrumentation
{
    public static bool Enabled;

    /// <summary>
    /// When true, suppresses the two Console.WriteLine timing prints inside
    /// <c>Parser.ParseTextsToDeck</c>. Separate from <see cref="Enabled"/> so
    /// non-stage benchmarks can still silence log noise.
    /// </summary>
    public static bool SuppressStageLogs;

    private static long _sudachiTicks;
    private static long _preprocessTicks;
    private static long _deconjugationLookupTicks;
    private static long _resegmentationTicks;
    private static long _adjacentScoringTicks;

    private static long _deconjugatorTicks;
    private static long _deconjugatorCalls;
    private static long _deconjugatorCacheHits;

    private static long _lookupHits;
    private static long _lookupMisses;
    private static long _wordCacheHits;
    private static long _wordCacheMisses;

    public static void Reset()
    {
        _sudachiTicks = 0;
        _preprocessTicks = 0;
        _deconjugationLookupTicks = 0;
        _resegmentationTicks = 0;
        _adjacentScoringTicks = 0;

        _deconjugatorTicks = 0;
        _deconjugatorCalls = 0;
        _deconjugatorCacheHits = 0;

        _lookupHits = 0;
        _lookupMisses = 0;
        _wordCacheHits = 0;
        _wordCacheMisses = 0;
    }

    public static long Now() => Enabled ? Stopwatch.GetTimestamp() : 0L;

    public static void AddSudachi(long startTs)             { if (Enabled) _sudachiTicks             += Stopwatch.GetTimestamp() - startTs; }
    public static void AddPreprocess(long startTs)          { if (Enabled) _preprocessTicks          += Stopwatch.GetTimestamp() - startTs; }
    public static void AddDeconjugationLookup(long startTs) { if (Enabled) _deconjugationLookupTicks += Stopwatch.GetTimestamp() - startTs; }
    public static void AddResegmentation(long startTs)      { if (Enabled) _resegmentationTicks      += Stopwatch.GetTimestamp() - startTs; }
    public static void AddAdjacentScoring(long startTs)     { if (Enabled) _adjacentScoringTicks     += Stopwatch.GetTimestamp() - startTs; }

    public static void AddDeconjugator(long startTs)        { if (Enabled) _deconjugatorTicks        += Stopwatch.GetTimestamp() - startTs; }

    public static void RecordDeconjugatorCall(bool cacheHit)
    {
        if (!Enabled) return;
        _deconjugatorCalls++;
        if (cacheHit) _deconjugatorCacheHits++;
    }

    public static void RecordLookupCacheResult(int hits, int misses)
    {
        if (!Enabled) return;
        _lookupHits += hits;
        _lookupMisses += misses;
    }

    public static void RecordWordCacheResult(int hits, int misses)
    {
        if (!Enabled) return;
        _wordCacheHits += hits;
        _wordCacheMisses += misses;
    }

    public record Snapshot(
        double SudachiMs,
        double PreprocessMs,
        double DeconjugationLookupMs,
        double ResegmentationMs,
        double AdjacentScoringMs,
        double DeconjugatorMs,
        long DeconjugatorCalls,
        long DeconjugatorCacheHits,
        long LookupHits,
        long LookupMisses,
        long WordCacheHits,
        long WordCacheMisses)
    {
        public double TotalInstrumentedMs =>
            SudachiMs + PreprocessMs + DeconjugationLookupMs + ResegmentationMs + AdjacentScoringMs;

        public double LookupHitRatio =>
            (LookupHits + LookupMisses) == 0 ? 0 : (double)LookupHits / (LookupHits + LookupMisses);

        public double WordCacheHitRatio =>
            (WordCacheHits + WordCacheMisses) == 0 ? 0 : (double)WordCacheHits / (WordCacheHits + WordCacheMisses);

        public double DeconjugatorCacheHitRatio =>
            DeconjugatorCalls == 0 ? 0 : (double)DeconjugatorCacheHits / DeconjugatorCalls;
    }

    public static Snapshot Capture()
    {
        double ToMs(long t) => t * 1000.0 / Stopwatch.Frequency;
        return new Snapshot(
            ToMs(_sudachiTicks),
            ToMs(_preprocessTicks),
            ToMs(_deconjugationLookupTicks),
            ToMs(_resegmentationTicks),
            ToMs(_adjacentScoringTicks),
            ToMs(_deconjugatorTicks),
            _deconjugatorCalls,
            _deconjugatorCacheHits,
            _lookupHits,
            _lookupMisses,
            _wordCacheHits,
            _wordCacheMisses);
    }
}
