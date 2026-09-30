using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services;

/// <summary>Sentence listings shared across a search's pages, capped by entry count so varied searches can't pin unbounded memory.</summary>
public sealed class ReadableSentenceListingCache : IDisposable
{
    /// <summary>Listing entries held across all searches; one entry is 16 bytes, so about 32 MB.</summary>
    private const long MaxEntries = 2_000_000;

    public MemoryCache Cache { get; private set; } = Create();

    private static MemoryCache Create() => new(new MemoryCacheOptions { SizeLimit = MaxEntries });

    public void Clear()
    {
        var old = Cache;
        Cache = Create();
        old.Dispose();
    }

    public void Dispose() => Cache.Dispose();
}

public interface IReadableSentenceSearch
{
    /// <summary>One page of the word's sentences the current user can read, resuming from the request's cursor.</summary>
    Task<ReadableSentencesResponse> SearchAsync(int wordId, byte readingIndex, ReadableSentencesRequest request, int[]? deckIds);
}

/// <summary>
/// Lists every sentence holding the word, then judges them one slice at a time in an order fixed by the search seed,
/// so each page resumes where the last one stopped and the whole set is covered by the end.
/// </summary>
public class ReadableSentenceSearch(
    JitenDbContext context,
    ISentenceTokenService sentenceTokens,
    IExampleSentenceQueryService exampleSentences,
    ReadableSentenceListingCache listings) : IReadableSentenceSearch
{
    public const int PageSize = 20;

    /// <summary>Sentences judged per request, which bounds the known-state lookup behind each page.</summary>
    private const int ScanBudget = 2000;

    private const int FineBuckets = ExampleSentenceTokens.FineBucketCount;

    /// <summary>Listings are shared across a search's pages; the commonest words list about 16k sentences.</summary>
    private static readonly TimeSpan ListingTtl = TimeSpan.FromMinutes(10);

    private sealed class ScanRow
    {
        public long SentenceId { get; set; }
        public float Difficulty { get; set; }
        public byte[] Tokens { get; set; } = [];
    }

    private sealed class BucketRow
    {
        public long SentenceId { get; set; }
        public int? Bucket { get; set; }
    }

    public async Task<ReadableSentencesResponse> SearchAsync(int wordId, byte readingIndex, ReadableSentencesRequest request, int[]? deckIds)
    {
        var key = ExampleSentenceTokens.WordKey(wordId, readingIndex);
        var unknown = Math.Clamp(request.Unknown, 0, 2);
        var start = (int)((uint)request.Seed % FineBuckets);

        var mediaTypes = request.MediaTypes.Where(Enum.IsDefined).Distinct().Order().ToArray();
        if (deckIds != null && mediaTypes.Length > 0)
        {
            deckIds = await context.Decks.AsNoTracking()
                                   .Where(d => deckIds.Contains(d.DeckId) && mediaTypes.Contains(d.MediaType))
                                   .Select(d => d.DeckId)
                                   .ToArrayAsync();
        }

        if (deckIds is { Length: 0 }) return new ReadableSentencesResponse();

        var listing = deckIds != null
            ? await ListInDecks(wordId, readingIndex, key, deckIds)
            : await ListAll(key, mediaTypes);

        // Rotated by the seed, so a new search starts on a different slice of the word's sentences
        var ordered = listing.Select(l => (l.SentenceId, Bucket: (l.Bucket - start + FineBuckets) % FineBuckets))
                             .OrderBy(l => l.Bucket)
                             .ThenBy(l => l.SentenceId)
                             .ToList();

        var bucket = Math.Clamp(request.Cursor?.Bucket ?? 0, 0, FineBuckets);
        var skip = Math.Max(request.Cursor?.Skip ?? 0, 0);
        var found = new List<(long SentenceId, List<SentenceToken> Unknown)>();
        var scanned = 0;

        while (bucket < FineBuckets && found.Count < PageSize && scanned < ScanBudget)
        {
            var end = SliceEnd(ordered, bucket);
            var ids = ordered.Where(l => l.Bucket >= bucket && l.Bucket < end).Select(l => l.SentenceId).ToList();
            var rows = await LoadRows(ids);
            scanned += rows.Count;

            var unknownTokens = await sentenceTokens.UnknownTokensAsync(rows.Select(r => (r.SentenceId, wordId, r.Tokens)));
            var hits = Order(rows.Where(r => unknownTokens.TryGetValue((r.SentenceId, wordId), out var u)
                                             && u.Select(t => t.WordKey).Distinct().Count() == unknown),
                             request.Sort, request.Seed ^ bucket)
                       .Skip(skip)
                       .Select(id => (id, unknownTokens[(id, wordId)]))
                       .ToList();

            var room = PageSize - found.Count;
            if (hits.Count > room)
            {
                found.AddRange(hits.Take(room));
                return await Page(found, unknown, wordId, readingIndex, new ReadableSentencesCursor { Bucket = bucket, Skip = skip + room });
            }

            found.AddRange(hits);
            bucket = end;
            skip = 0;
        }

        return await Page(found, unknown, wordId, readingIndex, bucket < FineBuckets ? new ReadableSentencesCursor { Bucket = bucket } : null);
    }

    /// <summary>End (exclusive) of a slice of whole buckets from the given one holding about one scan budget of sentences.</summary>
    private static int SliceEnd(List<(long SentenceId, int Bucket)> ordered, int bucket)
    {
        var first = ordered.FindIndex(l => l.Bucket >= bucket);
        var cut = first + ScanBudget;
        if (first < 0 || cut >= ordered.Count) return FineBuckets;

        return Math.Max(ordered[cut].Bucket, ordered[first].Bucket + 1);
    }

    private async Task<ReadableSentencesResponse> Page(List<(long SentenceId, List<SentenceToken> Unknown)> found, int unknown, int wordId,
                                                       byte readingIndex, ReadableSentencesCursor? next)
    {
        var dtos = await exampleSentences.BuildDtosAsync(found.Select(f => f.SentenceId).ToList(), wordId, readingIndex);
        var unknownById = found.ToDictionary(f => f.SentenceId, f => f.Unknown);
        foreach (var dto in dtos)
        {
            dto.UnknownCount = unknown;
            dto.IsIPlusOne = unknown == 0;
            dto.UnknownSpans = unknownById[dto.SentenceId]
                               .Select(t => new SentenceSpanDto { Position = t.Position, Length = t.Length })
                               .ToList();
        }

        return new ReadableSentencesResponse { Sentences = dtos, Next = next };
    }

    private static IEnumerable<long> Order(IEnumerable<ScanRow> hits, ReadableSentenceSort sort, int seed)
    {
        var ordered = sort switch
        {
            ReadableSentenceSort.EasiestFirst => hits.OrderBy(h => h.Difficulty),
            ReadableSentenceSort.HardestFirst => hits.OrderByDescending(h => h.Difficulty),
            _ => hits.OrderBy(h => Scramble(h.SentenceId, seed))
        };
        return ordered.ThenBy(h => h.SentenceId).Select(h => h.SentenceId);
    }

    /// <summary>A fixed shuffle key, so a slice re-read on the next page comes back in the same order; HashCode is per-process.</summary>
    private static ulong Scramble(long sentenceId, int seed)
    {
        var x = (ulong)sentenceId ^ ((ulong)(uint)seed << 32);
        x ^= x >> 33;
        x *= 0xff51afd7ed558ccdUL;
        x ^= x >> 33;
        x *= 0xc4ceb9fe1a85ec53UL;
        x ^= x >> 33;
        return x;
    }

    private bool IsNpgsql => context.Database.ProviderName?.Contains("Npgsql") == true;

    private Task<List<ScanRow>> LoadRows(List<long> ids)
    {
        if (ids.Count == 0) return Task.FromResult(new List<ScanRow>());
        return context.ExampleSentences.AsNoTracking()
                      .Where(s => ids.Contains(s.SentenceId))
                      .Select(s => new ScanRow { SentenceId = s.SentenceId, Difficulty = s.Difficulty, Tokens = s.Tokens })
                      .ToListAsync();
    }

    private Task<List<(long SentenceId, int Bucket)>> Cached(string cacheKey, Func<Task<List<BucketRow>>> list) =>
        listings.Cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            var listing = (await list()).Where(r => r.Bucket.HasValue).Select(r => (r.SentenceId, r.Bucket!.Value)).ToList();
            entry.AbsoluteExpirationRelativeToNow = ListingTtl;
            entry.Size = listing.Count + 1;
            return listing;
        })!;

    private const string FineBucketSql =
        @"(SELECT k FROM unnest(es.""WordKeys"") k WHERE k >= 64 AND k < 4160 LIMIT 1) - 64";

    private Task<List<(long SentenceId, int Bucket)>> ListAll(int key, MediaType[] mediaTypes) =>
        Cached($"readable:all:{key}:{string.Join(',', mediaTypes.Select(t => (int)t))}", async () =>
        {
            if (!IsNpgsql)
            {
                var query = context.ExampleSentences.AsNoTracking().Where(s => s.WordKeys.Contains(key));
                if (mediaTypes.Length > 0)
                    query = query.Where(s => context.Decks.Any(d => d.DeckId == s.DeckId && mediaTypes.Contains(d.MediaType)));
                return await InMemoryBuckets(query);
            }

            var mediaClause = mediaTypes.Length > 0
                ? @"AND EXISTS (SELECT 1 FROM jiten.""Decks"" d WHERE d.""DeckId"" = es.""DeckId"" AND d.""MediaType"" = ANY({1}))"
                : "";
            object[] parameters = mediaTypes.Length > 0 ? [key, mediaTypes.Select(t => (int)t).ToArray()] : [key];
            return await context.Database.SqlQueryRaw<BucketRow>($@"
                SELECT es.""SentenceId"", {FineBucketSql} AS ""Bucket"" FROM jiten.""ExampleSentences"" es
                WHERE es.""WordKeys"" @> ARRAY[{{0}}] {mediaClause}", parameters).ToListAsync();
        });

    private Task<List<(long SentenceId, int Bucket)>> ListInDecks(int wordId, byte readingIndex, int key, int[] deckIds) =>
        Cached($"readable:decks:{key}:{string.Join(',', deckIds.Order())}", async () =>
        {
            var held = await context.DeckWords.AsNoTracking()
                                    .Where(dw => dw.WordId == wordId && dw.ReadingIndex == readingIndex && deckIds.Contains(dw.DeckId))
                                    .Select(dw => dw.DeckId)
                                    .Distinct()
                                    .ToArrayAsync();
            if (held.Length == 0) return [];

            if (!IsNpgsql)
                return await InMemoryBuckets(context.ExampleSentences.AsNoTracking().Where(s => held.Contains(s.DeckId) && s.WordKeys.Contains(key)));

            return await context.Database.SqlQueryRaw<BucketRow>($@"
                SELECT es.""SentenceId"", {FineBucketSql} AS ""Bucket"" FROM jiten.""ExampleSentences"" es
                WHERE es.""DeckId"" = ANY({{0}}) AND es.""WordKeys"" @> ARRAY[{{1}}]", held, key).ToListAsync();
        });

    /// <summary>SQLite, which the integration tests run on, can't unnest; its tables are small enough to bucket client-side.</summary>
    private static async Task<List<BucketRow>> InMemoryBuckets(IQueryable<ExampleSentence> query) =>
        (await query.Select(s => new { s.SentenceId, s.WordKeys }).ToListAsync())
        .Select(s => new BucketRow { SentenceId = s.SentenceId, Bucket = ExampleSentenceTokens.FineBucketOf(s.WordKeys) })
        .ToList();
}
