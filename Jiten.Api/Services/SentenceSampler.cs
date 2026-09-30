using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>Random samples off the WordKeys GIN index; a fine bucket's size picks the pool, so a common word never sorts millions of rows.</summary>
public static class SentenceSampler
{
    /// <summary>Largest estimated match count still sorted whole.</summary>
    private const int WholeSetLimit = 16_000;

    private const int FinePerCoarse = ExampleSentenceTokens.FineBucketCount / ExampleSentenceTokens.CoarseBucketCount;

    private sealed class KeyedSentence
    {
        public int Key { get; set; }
        public long SentenceId { get; set; }
    }

    public static async Task<Dictionary<int, List<long>>> SampleAsync(JitenDbContext context, IReadOnlyCollection<int> keys, int want)
    {
        var result = new Dictionary<int, List<long>>();
        var distinct = keys.Distinct().ToArray();
        if (distinct.Length == 0) return result;

        // The integration tests run on SQLite, which has neither GIN nor LATERAL; their tables are small enough to sort whole
        if (context.Database.ProviderName?.Contains("Npgsql") != true)
        {
            foreach (var key in distinct)
            {
                result[key] = await context.ExampleSentences
                                           .Where(s => s.WordKeys.Contains(key))
                                           .OrderBy(_ => EF.Functions.Random())
                                           .Take(want)
                                           .Select(s => s.SentenceId)
                                           .ToListAsync();
            }

            return result;
        }

        var fineBuckets = distinct.Select(_ => Random.Shared.Next(ExampleSentenceTokens.FineBucketCount)).ToArray();
        var fine = await SampleBuckets(context, distinct, fineBuckets.Select(ExampleSentenceTokens.FineBucketKey).ToArray(), want);

        var whole = new List<int>();
        var coarse = new List<(int Key, int Bucket)>();
        for (int i = 0; i < distinct.Length; i++)
        {
            var key = distinct[i];
            var ids = fine.GetValueOrDefault(key, []);
            if (ids.Count >= want)
                result[key] = ids;
            else if ((long)ids.Count * ExampleSentenceTokens.FineBucketCount <= WholeSetLimit)
                whole.Add(key);
            else
                coarse.Add((key, fineBuckets[i] / FinePerCoarse));
        }

        if (coarse.Count > 0)
        {
            var coarseHits = await SampleBuckets(context, coarse.Select(c => c.Key).ToArray(),
                                                 coarse.Select(c => ExampleSentenceTokens.CoarseBucketKey(c.Bucket)).ToArray(), want);
            foreach (var (key, _) in coarse)
                result[key] = coarseHits.GetValueOrDefault(key, []);
        }

        if (whole.Count > 0)
        {
            var wholeHits = await Group(context.Database.SqlQueryRaw<KeyedSentence>(@"
                SELECT v.k AS ""Key"", s.""SentenceId"" AS ""SentenceId""
                FROM unnest({0}::int[]) AS v(k)
                CROSS JOIN LATERAL (
                    SELECT es.""SentenceId"" FROM jiten.""ExampleSentences"" es
                    WHERE es.""WordKeys"" @> ARRAY[v.k]
                    ORDER BY random()
                    LIMIT {1}
                ) s", whole.ToArray(), want));
            foreach (var key in whole)
                result[key] = wholeHits.GetValueOrDefault(key, []);
        }

        return result;
    }

    // ORDER BY random() is load-bearing: with a bare LIMIT the planner picks a full sequential scan for a runtime array key.
    private static Task<Dictionary<int, List<long>>> SampleBuckets(JitenDbContext context, int[] keys, int[] bucketKeys, int want) =>
        Group(context.Database.SqlQueryRaw<KeyedSentence>(@"
            SELECT v.k AS ""Key"", s.""SentenceId"" AS ""SentenceId""
            FROM unnest({0}::int[], {1}::int[]) AS v(k, b)
            CROSS JOIN LATERAL (
                SELECT es.""SentenceId"" FROM jiten.""ExampleSentences"" es
                WHERE es.""WordKeys"" @> ARRAY[v.k, v.b]
                ORDER BY random()
                LIMIT {2}
            ) s", keys, bucketKeys, want));

    private static async Task<Dictionary<int, List<long>>> Group(IQueryable<KeyedSentence> query) =>
        (await query.ToListAsync())
        .GroupBy(r => r.Key)
        .ToDictionary(g => g.Key, g => g.Select(r => r.SentenceId).ToList());
}
