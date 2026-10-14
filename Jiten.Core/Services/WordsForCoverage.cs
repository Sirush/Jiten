using System.Diagnostics;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jiten.Core.Services;

public sealed record WordsForCoverageResult(MediaType MediaType, int Titles, double[] MedianRanks, double MedianExcludedPercent);

/// <summary>Per media type, how far down the word frequency list a learner must know every word to reach each coverage level in a typical title.</summary>
public static class WordsForCoverage
{
    public static readonly double[] Levels = [0.80, 0.85, 0.90, 0.93, 0.95, 0.98, 0.99];

    public sealed record Options(double MinTypeShare = 0.01, int MinTypeTitles = 5, int MinCharacters = 5000, int SampleModulo = 1);

    public static async Task<List<WordsForCoverageResult>> ComputeAsync(IDbContextFactory<JitenDbContext> contextFactory, Options options,
                                                                       Action<string>? log = null, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var wordRanks = await context.JmDictWordFrequencies.AsNoTracking()
                                     .Where(f => f.FrequencyRank > 0)
                                     .ToDictionaryAsync(f => f.WordId, f => f.FrequencyRank, ct);

        var titlesPerType = await context.Decks.AsNoTracking()
                                         .Where(d => d.ParentDeckId == null)
                                         .GroupBy(d => d.MediaType)
                                         .Select(g => new { MediaType = g.Key, Count = g.Count() })
                                         .ToDictionaryAsync(g => g.MediaType, g => g.Count, ct);

        // Names and title-specific words would decide the high coverage levels, so only words common to the media type count.
        var commonWords = new Dictionary<MediaType, HashSet<int>>();
        foreach (var (mediaType, titleCount) in titlesPerType)
        {
            var minTitles = Math.Max((int)Math.Ceiling(options.MinTypeShare * titleCount), options.MinTypeTitles);
            var ids = await context.WordFrequenciesByType.AsNoTracking()
                                   .Where(f => f.MediaType == mediaType && f.UsedInMediaAmount >= minTitles)
                                   .Select(f => f.WordId)
                                   .ToListAsync(ct);
            commonWords[mediaType] = ids.ToHashSet();
        }

        log?.Invoke($"Loaded {wordRanks.Count:N0} word ranks and {commonWords.Values.Sum(s => s.Count):N0} common words in {stopwatch.Elapsed.TotalSeconds:F1}s");

        var perType = new Dictionary<MediaType, List<(int[] Ranks, double ExcludedPercent)>>();
        var buffer = new List<long>(16384);
        var ranks = new int[Levels.Length];
        int currentDeck = -1;
        MediaType currentType = default;
        long included = 0, excluded = 0, rows = 0;

        void FlushDeck()
        {
            if (currentDeck < 0 || included == 0)
                return;

            ThresholdRanks(buffer, included, Levels, ranks);
            if (!perType.TryGetValue(currentType, out var list))
                perType[currentType] = list = [];
            list.Add(((int[])ranks.Clone(), 100.0 * excluded / (included + excluded)));
        }

        await using var conn = new NpgsqlConnection(context.Database.GetConnectionString());
        await conn.OpenAsync(ct);

        var sampleFilter = options.SampleModulo > 1 ? $@" AND d.""DeckId"" % {options.SampleModulo} = 0" : "";
        var copy = $@"COPY (SELECT d.""DeckId"", d.""MediaType"", w.""WordId"", w.""Occurrences""
                           FROM jiten.""Decks"" d
                           JOIN jiten.""DeckWords"" w ON w.""DeckId"" = d.""DeckId""
                           WHERE d.""ParentDeckId"" IS NULL AND d.""CharacterCount"" >= {options.MinCharacters}{sampleFilter}
                           ORDER BY d.""DeckId"") TO STDOUT (FORMAT BINARY)";

        // Per-field async reads cost several times the whole scan here.
        using (var reader = conn.BeginBinaryExport(copy))
        {
            while (reader.StartRow() != -1)
            {
                ct.ThrowIfCancellationRequested();
                var deckId = reader.Read<int>();
                var mediaType = (MediaType)reader.Read<int>();
                var wordId = reader.Read<int>();
                var occurrences = reader.Read<int>();
                rows++;

                if (deckId != currentDeck)
                {
                    FlushDeck();
                    currentDeck = deckId;
                    currentType = mediaType;
                    buffer.Clear();
                    included = excluded = 0;
                }

                if (!commonWords.TryGetValue(mediaType, out var common) || !common.Contains(wordId))
                {
                    excluded += occurrences;
                    continue;
                }

                var rank = wordRanks.TryGetValue(wordId, out var r) ? r : int.MaxValue;
                buffer.Add(((long)rank << 32) | (uint)occurrences);
                included += occurrences;
            }
        }

        FlushDeck();
        log?.Invoke($"Streamed {rows:N0} deck words from {perType.Values.Sum(l => l.Count):N0} titles in {stopwatch.Elapsed.TotalSeconds:F1}s");

        return perType.OrderBy(kv => kv.Key)
                      .Select(kv => new WordsForCoverageResult(
                                  kv.Key,
                                  kv.Value.Count,
                                  Enumerable.Range(0, Levels.Length).Select(i => Median(kv.Value.Select(t => (double)t.Ranks[i]))).ToArray(),
                                  Median(kv.Value.Select(t => t.ExcludedPercent))))
                      .ToList();
    }

    /// <summary>Entries pack rank in the high 32 bits and occurrences in the low 32; sorts them in place. A level never reached gets int.MaxValue.</summary>
    public static void ThresholdRanks(List<long> rankedOccurrences, long totalOccurrences, IReadOnlyList<double> levels, int[] result)
    {
        rankedOccurrences.Sort();
        Array.Fill(result, int.MaxValue);

        long cumulative = 0;
        var level = 0;
        foreach (var entry in rankedOccurrences)
        {
            cumulative += entry & 0xFFFFFFFF;
            while (level < levels.Count && cumulative >= levels[level] * totalOccurrences)
                result[level++] = (int)(entry >> 32);
            if (level == levels.Count)
                break;
        }
    }

    /// <summary>Interpolated median, matching Postgres PERCENTILE_CONT(0.5).</summary>
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0)
            return double.NaN;
        var mid = (sorted.Length - 1) / 2.0;
        return (sorted[(int)Math.Floor(mid)] + sorted[(int)Math.Ceiling(mid)]) / 2;
    }
}
