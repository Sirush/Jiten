using System.Threading.Channels;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>Readable and i+1 sentence shares per top-level deck, stored as coverage chunk metrics for the browse bars and sorts.</summary>
public static class SentenceCoverageWriter
{
    public const short NoData = -1;

    private const int ChunkSize = 1024;

    /// <summary>Parallel readers for the full pass.</summary>
    private const int ReaderCount = 4;

    private sealed class KnownRow
    {
        public int WordId { get; set; }
        public int ReadingIndex { get; set; }
    }

    /// <summary>One sentence blob and the top-level deck it counts towards.</summary>
    public sealed class RootBlob
    {
        public int Root { get; set; }
        public byte[]? Blob { get; set; }
    }

    /// <summary>Reads the coverage job's known-word temp tables, so it must run inside that job's transaction.</summary>
    public static async Task WriteAsync(UserDbContext userContext, IDbContextFactory<JitenDbContext> contextFactory,
                                        string userId, DateTime computedAt)
    {
        var known = await LoadKnownKeys(userContext);

        int maxDeckId;
        await using (var context = await contextFactory.CreateDbContextAsync())
            maxDeckId = await context.Decks.MaxAsync(d => (int?)d.DeckId) ?? 0;

        var counts = await CountByRootAsync(contextFactory, known);
        var values = counts.Where(kv => kv.Value.Total > 0)
                           .Select(kv => (kv.Key, BasisPoints(kv.Value.Readable, kv.Value.Total), BasisPoints(kv.Value.OneUnknown, kv.Value.Total)))
                           .ToList();

        userContext.UserCoverageChunks.AddRange(BuildChunks(values, maxDeckId, userId, computedAt));
        await userContext.SaveChangesAsync();
    }

    /// <summary>
    /// Refreshes the given decks' slots in chunks an earlier full recompute wrote; a user without them is left for that recompute.
    /// Reads the known-word temp tables, so it must run inside the transaction that created them.
    /// </summary>
    public static async Task UpdateDecksAsync(UserDbContext userContext, IDbContextFactory<JitenDbContext> contextFactory,
                                              string userId, IReadOnlyCollection<int> deckIds, DateTime computedAt)
    {
        var chunkIndices = deckIds.Select(id => id / ChunkSize).Distinct().ToList();
        short[] metrics = [(short)UserCoverageMetric.ReadableSentences, (short)UserCoverageMetric.IPlusOneSentences];
        var chunks = await userContext.UserCoverageChunks
                                      .Where(c => c.UserId == userId && metrics.Contains(c.Metric) && chunkIndices.Contains(c.ChunkIndex))
                                      .ToListAsync();
        if (chunks.Count == 0) return;

        var known = await LoadKnownKeys(userContext);
        var counts = await CountByRootAsync(contextFactory, known, deckIds);

        foreach (var chunk in chunks)
        {
            var values = chunk.Values.ToArray();
            foreach (var deckId in deckIds.Where(id => id / ChunkSize == chunk.ChunkIndex))
            {
                values[deckId % ChunkSize] = !counts.TryGetValue(deckId, out var c) || c.Total == 0
                    ? NoData
                    : BasisPoints(chunk.Metric == (short)UserCoverageMetric.ReadableSentences ? c.Readable : c.OneUnknown, c.Total);
            }

            chunk.Values = values;
            chunk.ComputedAt = computedAt;
        }

        await userContext.SaveChangesAsync();
    }

    /// <summary>
    /// Sentence counts per top-level deck: its stored sample when the title is too long to read whole, otherwise every sentence of its
    /// own text or its children's. <paramref name="rootIds"/> limits the pass to those decks; without it the whole catalogue is read.
    /// </summary>
    public static async Task<Dictionary<int, (int Total, int Readable, int OneUnknown)>> CountByRootAsync(
        IDbContextFactory<JitenDbContext> contextFactory, IReadOnlySet<int> known, IReadOnlyCollection<int>? rootIds = null)
    {
        var channel = Channel.CreateBounded<RootBlob>(new BoundedChannelOptions(512) { SingleWriter = false });
        int readers = rootIds == null ? ReaderCount : 1;
        // A failed decoder stops consuming; without this the readers would wait on the full channel forever.
        using var abort = new CancellationTokenSource();

        var reading = Task.Run(async () =>
        {
            try
            {
                var options = new ParallelOptions { MaxDegreeOfParallelism = readers, CancellationToken = abort.Token };
                await Parallel.ForEachAsync(Enumerable.Range(0, readers), options, async (reader, ct) =>
                {
                    await using var context = await contextFactory.CreateDbContextAsync(ct);
                    context.Database.SetCommandTimeout(TimeSpan.FromMinutes(3));
                    var query = RootBlobs(context);
                    query = rootIds == null
                        ? query.Where(b => b.Root % readers == reader)
                        : query.Where(b => rootIds.Contains(b.Root));

                    await foreach (var blob in query.AsAsyncEnumerable().WithCancellation(ct))
                        await channel.Writer.WriteAsync(blob, ct);
                });
                channel.Writer.Complete();
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
            }
        });

        var decoders = Enumerable.Range(0, Math.Max(2, Environment.ProcessorCount / 2)).Select(_ => Task.Run(async () =>
        {
            var partial = new Dictionary<int, (int Total, int Readable, int OneUnknown)>();
            try
            {
                await foreach (var blob in channel.Reader.ReadAllAsync())
                {
                    var (total, readable, oneUnknown) = SentenceProfileCodec.CountReadable(blob.Blob!, known.Contains);
                    var sum = partial.GetValueOrDefault(blob.Root);
                    partial[blob.Root] = (sum.Total + total, sum.Readable + readable, sum.OneUnknown + oneUnknown);
                }
            }
            catch
            {
                await abort.CancelAsync();
                throw;
            }

            return partial;
        })).ToList();

        await reading;
        var result = new Dictionary<int, (int Total, int Readable, int OneUnknown)>();
        foreach (var partial in await Task.WhenAll(decoders))
        foreach (var (root, (total, readable, oneUnknown)) in partial)
        {
            var sum = result.GetValueOrDefault(root);
            result[root] = (sum.Total + total, sum.Readable + readable, sum.OneUnknown + oneUnknown);
        }

        return result;
    }

    /// <summary>A title with a sample contributes only the sample; otherwise each of its texts contributes its full profile.</summary>
    private static IQueryable<RootBlob> RootBlobs(JitenDbContext context)
    {
        var sampledRoots = context.DeckSentenceProfiles.Where(p => p.Sample != null).Select(p => p.DeckId);

        var fromProfiles = from p in context.DeckSentenceProfiles
                           join d in context.Decks on p.DeckId equals d.DeckId
                           where p.Profile != null && !sampledRoots.Contains(d.ParentDeckId ?? d.DeckId)
                           select new RootBlob { Root = d.ParentDeckId ?? d.DeckId, Blob = p.Profile };

        var fromSamples = from p in context.DeckSentenceProfiles
                          join d in context.Decks on p.DeckId equals d.DeckId
                          where p.Sample != null && d.ParentDeckId == null
                          select new RootBlob { Root = p.DeckId, Blob = p.Sample };

        return fromProfiles.Concat(fromSamples).AsNoTracking();
    }

    private static async Task<HashSet<int>> LoadKnownKeys(UserDbContext userContext)
    {
        var knownRows = await userContext.Database.SqlQueryRaw<KnownRow>("""
            SELECT "WordId", "ReadingIndex"::int AS "ReadingIndex" FROM _mature_known
            UNION
            SELECT "WordId", "ReadingIndex"::int FROM _fsrs_young
            """).ToListAsync();
        return knownRows.Select(r => ExampleSentenceTokens.WordKey(r.WordId, (byte)r.ReadingIndex)).ToHashSet();
    }

    public static List<UserCoverageChunk> BuildChunks(IReadOnlyCollection<(int DeckId, short Readable, short IPlusOne)> values,
                                                      int maxDeckId, string userId, DateTime computedAt)
    {
        int chunkCount = maxDeckId / ChunkSize + 1;
        var readable = NewMetric(chunkCount);
        var iPlusOne = NewMetric(chunkCount);
        foreach (var (deckId, r, i) in values)
        {
            if (deckId / ChunkSize >= chunkCount) continue;
            readable[deckId / ChunkSize][deckId % ChunkSize] = r;
            iPlusOne[deckId / ChunkSize][deckId % ChunkSize] = i;
        }

        var chunks = new List<UserCoverageChunk>(chunkCount * 2);
        for (int c = 0; c < chunkCount; c++)
        {
            chunks.Add(new UserCoverageChunk
            {
                UserId = userId, Metric = (short)UserCoverageMetric.ReadableSentences, ChunkIndex = c, Values = readable[c], ComputedAt = computedAt
            });
            chunks.Add(new UserCoverageChunk
            {
                UserId = userId, Metric = (short)UserCoverageMetric.IPlusOneSentences, ChunkIndex = c, Values = iPlusOne[c], ComputedAt = computedAt
            });
        }

        return chunks;
    }

    private static short[][] NewMetric(int chunkCount)
    {
        var metric = new short[chunkCount][];
        for (int c = 0; c < chunkCount; c++)
        {
            metric[c] = new short[ChunkSize];
            Array.Fill(metric[c], NoData);
        }

        return metric;
    }

    private static short BasisPoints(int count, int total) => (short)Math.Round(count * 10000.0 / total);
}
