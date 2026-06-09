using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.User;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Jiten.Api.Services;

public interface IStudyDeckMembershipService
{
    /// <summary>
    /// For each requested word key, returns the ids of the current user's active study decks that contain it.
    /// Membership is defined independently of the user's review/known state (see notes on TargetCoverage below).
    /// </summary>
    Task<Dictionary<(int WordId, byte ReadingIndex), List<int>>> GetDeckMembership(
        IEnumerable<(int WordId, byte ReadingIndex)> keys);

    /// <summary>Evicts the cached materialised word set for a media study deck (call on config change / removal).</summary>
    Task Invalidate(string userId, int studyDeckId);
}

public class StudyDeckMembershipService(
    JitenDbContext context,
    UserDbContext userContext,
    ICurrentUserService currentUserService,
    IDeckWordResolver deckWordResolver,
    IConnectionMultiplexer redis,
    IMemoryCache memoryCache,
    ILogger<StudyDeckMembershipService> logger) : IStudyDeckMembershipService
{
    // No long TTL: correctness comes from active invalidation on deck modification. L1 bounds cross-instance
    // staleness; L2 is a backstop that also catches rare underlying-content changes (e.g. an admin re-parse).
    private static readonly TimeSpan L1Ttl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan L2Ttl = TimeSpan.FromHours(6);

    private static string CacheKey(string userId, int studyDeckId) => $"deckset:{userId}:{studyDeckId}";

    public async Task<Dictionary<(int WordId, byte ReadingIndex), List<int>>> GetDeckMembership(
        IEnumerable<(int WordId, byte ReadingIndex)> keys)
    {
        var result = new Dictionary<(int WordId, byte ReadingIndex), List<int>>();
        if (!currentUserService.IsAuthenticated)
            return result;

        var userId = currentUserService.UserId!;
        var keysSet = keys.ToHashSet();
        if (keysSet.Count == 0)
            return result;

        var studyDecks = await userContext.UserStudyDecks
            .AsNoTracking()
            .Where(sd => sd.UserId == userId && sd.IsActive)
            .ToListAsync();
        if (studyDecks.Count == 0)
            return result;

        var wordIds = keysSet.Select(k => k.WordId).Distinct().ToList();

        // Static word-list decks: direct, exact, scoped query. Cheap and always current, so never cached.
        var staticDeckIds = studyDecks
            .Where(sd => sd.DeckType == StudyDeckType.StaticWordList)
            .Select(sd => sd.UserStudyDeckId)
            .ToList();
        if (staticDeckIds.Count > 0)
        {
            var rows = await userContext.UserStudyDeckWords
                .AsNoTracking()
                .Where(w => staticDeckIds.Contains(w.UserStudyDeckId) && wordIds.Contains(w.WordId))
                .Select(w => new { w.UserStudyDeckId, w.WordId, w.ReadingIndex })
                .ToListAsync();

            foreach (var row in rows)
            {
                if (row.ReadingIndex < 0 || row.ReadingIndex > byte.MaxValue)
                    continue;
                var key = (row.WordId, (byte)row.ReadingIndex);
                if (keysSet.Contains(key))
                    AddMembership(result, key, row.UserStudyDeckId);
            }
        }

        var encoded = keysSet
            .Select(k => (Key: k, Encoded: WordFormHelper.EncodeWordKey(k.WordId, k.ReadingIndex)))
            .ToList();

        // Global frequency decks: membership of the parsed words is a bounded, scoped query — no caching needed.
        foreach (var sd in studyDecks.Where(sd => sd.DeckType == StudyDeckType.GlobalDynamic))
        {
            var matched = await deckWordResolver.GetGlobalDynamicWordKeysForWordIds(
                sd.MinGlobalFrequency, sd.MaxGlobalFrequency, sd.PosFilter, wordIds, sd.ExcludeKana);
            if (matched.Count == 0)
                continue;

            foreach (var (key, enc) in encoded)
                if (matched.Contains(enc))
                    AddMembership(result, key, sd.UserStudyDeckId);
        }

        // Media decks: the resolved set can't be cheaply scoped (TargetCoverage needs the whole deck ordered),
        // so the full word-key set is materialised once and cached, then intersected in-process.
        var mediaDecks = studyDecks
            .Where(sd => sd.DeckType == StudyDeckType.MediaDeck && sd.DeckId.HasValue)
            .ToList();
        foreach (var sd in mediaDecks)
        {
            var deckSet = await GetCachedMediaDeckSet(userId, sd);
            if (deckSet.Count == 0)
                continue;

            foreach (var (key, enc) in encoded)
                if (deckSet.Contains(enc))
                    AddMembership(result, key, sd.UserStudyDeckId);
        }

        return result;
    }

    public async Task Invalidate(string userId, int studyDeckId)
    {
        var cacheKey = CacheKey(userId, studyDeckId);
        memoryCache.Remove(cacheKey);
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(cacheKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed deleting deck-set cache for {CacheKey}", cacheKey);
        }
    }

    private static void AddMembership(
        Dictionary<(int WordId, byte ReadingIndex), List<int>> result,
        (int WordId, byte ReadingIndex) key, int deckId)
    {
        if (!result.TryGetValue(key, out var list))
        {
            list = new List<int>();
            result[key] = list;
        }

        if (!list.Contains(deckId))
            list.Add(deckId);
    }

    private async Task<HashSet<long>> GetCachedMediaDeckSet(string userId, UserStudyDeck sd)
    {
        var cacheKey = CacheKey(userId, sd.UserStudyDeckId);

        if (memoryCache.TryGetValue(cacheKey, out HashSet<long>? l1) && l1 != null)
            return l1;

        var db = redis.GetDatabase();
        try
        {
            var cached = await db.StringGetAsync(cacheKey);
            if (!cached.IsNullOrEmpty)
            {
                var set = MessagePackSerializer.Deserialize<long[]>((byte[])cached!).ToHashSet();
                memoryCache.Set(cacheKey, set, L1Ttl);
                return set;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed reading deck-set cache for {CacheKey}", cacheKey);
        }

        var materialised = await MaterialiseMediaDeckSet(sd);

        try
        {
            var bytes = MessagePackSerializer.Serialize(materialised.ToArray());
            await db.StringSetAsync(cacheKey, bytes, expiry: L2Ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed writing deck-set cache for {CacheKey}", cacheKey);
        }

        memoryCache.Set(cacheKey, materialised, L1Ttl);
        return materialised;
    }

    private async Task<HashSet<long>> MaterialiseMediaDeckSet(UserStudyDeck sd)
    {
        if (!sd.DeckId.HasValue)
            return [];

        // Only WordCount is needed downstream; project it (single row) rather than fetching the wide Deck row.
        // This runs only on a cache miss, keeping the hot cache-hit path free of any Decks query.
        var wordCount = await context.Decks
            .AsNoTracking()
            .Where(d => d.DeckId == sd.DeckId.Value)
            .Select(d => (int?)d.WordCount)
            .FirstOrDefaultAsync();
        if (wordCount is null)
            return [];

        var deck = new Deck { DeckId = sd.DeckId.Value, WordCount = wordCount.Value };

        if ((DeckDownloadType)sd.DownloadType == DeckDownloadType.TargetCoverage && sd.TargetPercentage.HasValue)
        {
            // Review-independent on purpose: startFromKnown is forced false so the set depends only on the deck's
            // configuration and content, never on the user's known-words. This keeps the cache valid across reviews.
            var (_, coverageKeys) = await deckWordResolver.CountTargetCoverageWords(
                sd.DeckId.Value, deck, sd.TargetPercentage.Value, sd.ExcludeKana, sd.PosFilter, startFromKnown: false);
            return coverageKeys;
        }

        var request = new DeckWordResolveRequest(
            sd.DeckId.Value, deck,
            (DeckDownloadType)sd.DownloadType, (DeckOrder)sd.Order,
            sd.MinFrequency, sd.MaxFrequency,
            false, false,
            sd.TargetPercentage,
            sd.MinOccurrences, sd.MaxOccurrences,
            sd.PosFilter, sd.StartFromKnown);
        var (_, mediaKeys) = await deckWordResolver.CountDeckWords(request, sd.ExcludeKana);
        return mediaKeys;
    }
}
