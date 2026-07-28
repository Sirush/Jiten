using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Jiten.Api.Services;

public interface ICoverageJourneyService
{
    /// <summary>Null when the deck does not exist.</summary>
    Task<JourneyDto?> GetDeckJourneyAsync(string userId, int deckId, CancellationToken ct = default);

    Task<GlobalGrowthDto> GetGlobalGrowthAsync(string userId, CancellationToken ct = default);
}

public class CoverageJourneyService(
    JitenDbContext context,
    UserDbContext userContext,
    IConnectionMultiplexer redis,
    IMemoryCache memoryCache,
    ILogger<CoverageJourneyService> logger) : ICoverageJourneyService
{
    public const string MeterName = "Jiten.Api.CoverageJourney";

    /// <summary>Version-suffixed: entries written before the series moved from counting cards to counting words are not comparable.</summary>
    public const string GrowthCacheKeyPrefix = "journey:growth:v2:";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("jiten.journey.requests");

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);
    /// <summary>Without a coverage stamp nothing rotates the key, so unstamped entries expire on their own instead.</summary>
    private static readonly TimeSpan UnstampedCacheTtl = TimeSpan.FromMinutes(15);
    /// <summary>The growth series tracks reviews, not coverage, so it has no stamp to rotate on and just ages out.</summary>
    private static readonly TimeSpan GrowthCacheTtl = TimeSpan.FromHours(1);
    /// <summary>Transition dates never change once set, so they survive a week; a miss only costs one log walk.</summary>
    private static readonly TimeSpan TransitionDatesTtl = TimeSpan.FromDays(7);
    /// <summary>Absorbs a burst of deck views without rebuilding the segment map from the card rows each time.</summary>
    private static readonly TimeSpan SegmentsL1Ttl = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan MatureInterval = TimeSpan.FromDays(RetentionCalculator.MatureThresholdDays);
    private static readonly MessagePackSerializerOptions MapOptions =
        MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4Block);

    /// <summary>The walk stops here; cards past it fall back to their last review as their first.</summary>
    private const int MaxWalkableReviews = 2_000_000;

    private const double ParityTolerancePoints = 0.5;
    private const int DriftCheckSampleRate = 100;
    private static readonly TimeSpan ColdComputeWarningThreshold = TimeSpan.FromSeconds(2);

    private readonly record struct TransitionDates(DateOnly FirstReview, DateOnly? Matured);

    /// <summary>
    /// Per card, without the kana expansion: a word count must not count one card twice. DirectPairs covers every
    /// card held, including new ones contributing no segment, since a word set loses to a card in any state.
    /// GrowthFlat folds the same history to one entry per word and drops blacklisted cards, so the growth series
    /// counts the population the profile's known-word total counts rather than the cards behind it.
    /// </summary>
    private sealed record CardSegments(
        List<(int WordId, byte ReadingIndex, List<KnownSegment> Segments)> ByCard,
        List<KnownSegment> GrowthFlat,
        HashSet<(int WordId, byte ReadingIndex)> DirectPairs);

    public async Task<JourneyDto?> GetDeckJourneyAsync(string userId, int deckId, CancellationToken ct = default)
    {
        var deck = await context.Decks.AsNoTracking()
                                .Where(d => d.DeckId == deckId)
                                .Select(d => new { d.WordCount, d.UniqueWordCount })
                                .FirstOrDefaultAsync(ct);
        if (deck == null)
            return null;

        var stamp = await GetCoverageStampAsync(userId, ct);
        var cacheKey = $"journey:deck:{userId}:{deckId}:{StampKey(stamp)}";

        var cached = await ReadJsonAsync<JourneyDto>(cacheKey);
        if (cached != null)
        {
            Requests.Add(1, new KeyValuePair<string, object?>("cache", "hit"), new KeyValuePair<string, object?>("scope", "deck"));
            return cached;
        }

        Requests.Add(1, new KeyValuePair<string, object?>("cache", "miss"), new KeyValuePair<string, object?>("scope", "deck"));
        var started = Stopwatch.StartNew();

        var cards = await GetCardSegmentsAsync(userId, stamp, ct);
        var byPair = await GetPairMapAsync(userId, stamp, cards, ct);

        var entries = await context.DeckWords.AsNoTracking()
                                   .Where(dw => dw.DeckId == deckId)
                                   .Select(dw => new DeckWordEntry(dw.WordId, dw.ReadingIndex, dw.Occurrences))
                                   .ToListAsync(ct);

        var journey = CoverageJourneyBuilder.BuildDeckJourney(
            deckId, entries, byPair, deck.WordCount, deck.UniqueWordCount, DateOnly.FromDateTime(DateTime.UtcNow));
        journey.AsOf = stamp;

        // Sampled: the signal is statistical, and the check costs a chunk read on a per-user endpoint.
        if (stamp != null && Random.Shared.Next(DriftCheckSampleRate) == 0)
            await WarnOnCoverageDriftAsync(userId, deckId, journey);

        await WriteJsonAsync(cacheKey, journey, TtlFor(stamp));
        LogIfSlow(started, deckId);
        return journey;
    }

    /// <summary>Counts distinct words rather than the coverage known-set's kana-expanded pairs.</summary>
    public async Task<GlobalGrowthDto> GetGlobalGrowthAsync(string userId, CancellationToken ct = default)
    {
        var cacheKey = GrowthCacheKeyPrefix + userId;

        var cached = await ReadJsonAsync<GlobalGrowthDto>(cacheKey);
        if (cached != null)
        {
            Requests.Add(1, new KeyValuePair<string, object?>("cache", "hit"), new KeyValuePair<string, object?>("scope", "global"));
            return cached;
        }

        Requests.Add(1, new KeyValuePair<string, object?>("cache", "miss"), new KeyValuePair<string, object?>("scope", "global"));
        var started = Stopwatch.StartNew();

        var stamp = await GetCoverageStampAsync(userId, ct);
        var cards = await GetCardSegmentsAsync(userId, stamp, ct);
        var growth = CoverageJourneyBuilder.BuildGlobalGrowth(cards.GrowthFlat, DateOnly.FromDateTime(DateTime.UtcNow));

        await WriteJsonAsync(cacheKey, growth, GrowthCacheTtl);
        LogIfSlow(started, null);
        return growth;
    }

    /// <summary>
    /// Keyed on the coverage stamp as well as the user: a coverage refresh has to invalidate this at once,
    /// or the journey rebuilt right after one would still be assembled from pre-refresh card state.
    /// </summary>
    private async Task<CardSegments> GetCardSegmentsAsync(string userId, DateTime? stamp, CancellationToken ct)
    {
        var l1Key = $"journey:cards:{userId}:{StampKey(stamp)}";
        if (memoryCache.TryGetValue(l1Key, out CardSegments? cached) && cached != null)
            return cached;

        var segments = await BuildCardSegmentsAsync(userId, ct);
        memoryCache.Set(l1Key, segments, SegmentsL1Ttl);
        return segments;
    }

    /// <summary>
    /// Expands the per-card segments across each word's kana forms and folds in word sets, which is what
    /// coverage against a deck's (WordId, ReadingIndex) pairs needs and a card count does not.
    /// </summary>
    private async Task<Dictionary<(int WordId, byte ReadingIndex), List<KnownSegment>>> GetPairMapAsync(
        string userId, DateTime? stamp, CardSegments cards, CancellationToken ct)
    {
        var l1Key = $"journey:pairs:{userId}:{StampKey(stamp)}";
        if (memoryCache.TryGetValue(l1Key, out Dictionary<(int WordId, byte ReadingIndex), List<KnownSegment>>? cached) && cached != null)
            return cached;

        var byPair = new Dictionary<(int WordId, byte ReadingIndex), List<KnownSegment>>();

        if (cards.ByCard.Count > 0)
        {
            var wordIds = cards.ByCard.Select(c => c.WordId).Distinct().ToList();
            var forms = await context.WordForms.AsNoTracking()
                                     .Where(f => wordIds.Contains(f.WordId))
                                     .Select(f => new { f.WordId, f.ReadingIndex, f.FormType })
                                     .ToListAsync(ct);

            var kanjiPairs = forms.Where(f => f.FormType == JmDictFormType.KanjiForm)
                                  .Select(f => (f.WordId, ReadingIndex: (byte)f.ReadingIndex))
                                  .ToHashSet();
            var kanaFormsByWord = forms.Where(f => f.FormType == JmDictFormType.KanaForm)
                                       .GroupBy(f => f.WordId)
                                       .ToDictionary(g => g.Key, g => g.Select(f => (byte)f.ReadingIndex).Distinct().ToList());

            foreach (var (wordId, readingIndex, segments) in cards.ByCard)
            {
                Append((wordId, readingIndex), segments);

                if (!kanjiPairs.Contains((wordId, readingIndex)) || !kanaFormsByWord.TryGetValue(wordId, out var kanaIndices))
                    continue;

                foreach (var kanaIndex in kanaIndices)
                    Append((wordId, kanaIndex), segments);
            }
        }

        await AddWordSetSegmentsAsync(userId, byPair, cards.DirectPairs, ct);

        foreach (var pair in byPair.Keys.ToArray())
            byPair[pair] = CoverageJourneyBuilder.MergePairSegments(byPair[pair]);

        memoryCache.Set(l1Key, byPair, SegmentsL1Ttl);
        return byPair;

        void Append((int WordId, byte ReadingIndex) pair, List<KnownSegment> segments)
        {
            if (!byPair.TryGetValue(pair, out var list))
            {
                list = new List<KnownSegment>(segments.Count);
                byPair[pair] = list;
            }

            list.AddRange(segments);
        }
    }

    /// <summary>
    /// Turns each card into at most three state segments. The transition dates come from the cached walk;
    /// the tail comes from the live card row, so the final bucket always matches the stored coverage even
    /// when the cached dates are a week old.
    /// </summary>
    private async Task<CardSegments> BuildCardSegmentsAsync(string userId, CancellationToken ct)
    {
        var cards = await userContext.FsrsCards.AsNoTracking()
                                     .Where(c => c.UserId == userId)
                                     .Select(c => new { c.CardId, c.WordId, c.ReadingIndex, c.State, c.Due, c.LastReview, c.CreatedAt })
                                     .ToListAsync(ct);

        var byCard = new List<(int, byte, List<KnownSegment>)>(cards.Count);
        var byWord = new Dictionary<int, List<KnownSegment>>();
        var directPairs = cards.Select(c => (c.WordId, c.ReadingIndex)).ToHashSet();
        if (cards.Count == 0)
            return new CardSegments(byCard, [], directPairs);

        var dates = await GetTransitionDatesAsync(userId, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var card in cards)
        {
            TransitionDates? cached = dates.TryGetValue(card.CardId, out var cardDates) ? cardDates : null;
            var segments = DeriveSegments(card.State, card.Due, card.LastReview, card.CreatedAt, cached, today);
            if (segments.Count == 0) continue;

            byCard.Add((card.WordId, card.ReadingIndex, segments));

            if (card.State == FsrsState.Blacklisted) continue;
            if (!byWord.TryGetValue(card.WordId, out var wordSegments))
                byWord[card.WordId] = wordSegments = new List<KnownSegment>();
            wordSegments.AddRange(segments);
        }

        var growthFlat = new List<KnownSegment>(byWord.Count * 2);
        foreach (var wordSegments in byWord.Values)
            growthFlat.AddRange(CoverageJourneyBuilder.MergePairSegments(wordSegments));

        return new CardSegments(byCard, growthFlat, directPairs);
    }

    private static List<KnownSegment> DeriveSegments(
        FsrsState state, DateTime due, DateTime? lastReview, DateTime createdAt, TransitionDates? cached, DateOnly today)
    {
        // Blacklisted and Mastered only; _mature_known keeps Suspended on its interval-derived tier.
        var alwaysMature = state is FsrsState.Blacklisted or FsrsState.Mastered;
        var finalInterval = lastReview.HasValue ? due - lastReview.Value : (TimeSpan?)null;
        var matureNow = alwaysMature || (finalInterval.HasValue && finalInterval.Value >= MatureInterval);
        // Mirrors _fsrs_young in CoverageComputeService, which is what the coverage bars count: a card whose
        // schedule was reset is back in the new queue and counts as neither mature nor young.
        var youngNow = !matureNow && lastReview.HasValue
                                  && state is FsrsState.Learning or FsrsState.Review or FsrsState.Relearning or FsrsState.Suspended;

        DateOnly first;
        DateOnly? matured;

        if (cached is { FirstReview: var cachedFirst } && cachedFirst != default)
        {
            first = cachedFirst;
            matured = cached.Value.Matured;
        }
        else if (lastReview.HasValue)
        {
            // Reviewed since the dates were last walked. Dating it at that review keeps a new card
            // visible immediately instead of waiting out the cache; the next walk corrects the date.
            first = DateOnly.FromDateTime(lastReview.Value);
            matured = null;
        }
        else
        {
            // Marked known without ever being reviewed; anything else is still a new card.
            return alwaysMature ? [new KnownSegment(DateOnly.FromDateTime(createdAt), null, true)] : [];
        }

        var lastReviewDay = lastReview.HasValue ? DateOnly.FromDateTime(lastReview.Value) : first;
        // A card that matured since the dates were cached shows its crossing at its last review, which is
        // where it actually happened; the error only ever affects cards that matured within the TTL.
        matured ??= matureNow ? lastReviewDay : null;

        // A card holding neither state closes its history on the day it was reset: ResetCardSchedule leaves Due
        // at that moment, so the word drops out of the series then rather than staying young for ever.
        DateOnly? resetAt = matureNow || youngNow ? null : Min(DateOnly.FromDateTime(due), today);
        if (resetAt is { } closed && closed <= first)
            return [];

        var result = new List<KnownSegment>(3);
        if (matured is null)
        {
            result.Add(new KnownSegment(first, resetAt, false));
            return result;
        }

        var maturedAt = matured.Value < first ? first : matured.Value;
        if (resetAt is { } beforeCrossing && beforeCrossing <= maturedAt)
        {
            result.Add(new KnownSegment(first, beforeCrossing, false));
            return result;
        }

        if (maturedAt > first)
            result.Add(new KnownSegment(first, maturedAt, false));

        if (matureNow)
        {
            result.Add(new KnownSegment(maturedAt, null, true));
            return result;
        }

        if (resetAt is { } afterCrossing)
        {
            result.Add(new KnownSegment(maturedAt, afterCrossing, true));
            return result;
        }

        // Lapsed: mature until the review that dropped it, young from there.
        var lapsedAt = lastReviewDay < maturedAt ? maturedAt : lastReviewDay;
        if (lapsedAt > maturedAt)
            result.Add(new KnownSegment(maturedAt, lapsedAt, true));
        result.Add(new KnownSegment(lapsedAt, null, false));
        return result;
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    private async Task AddWordSetSegmentsAsync(
        string userId,
        Dictionary<(int WordId, byte ReadingIndex), List<KnownSegment>> byPair,
        HashSet<(int WordId, byte ReadingIndex)> directCardPairs,
        CancellationToken ct)
    {
        var setStates = await userContext.UserWordSetStates.AsNoTracking()
                                         .Where(s => s.UserId == userId)
                                         .Select(s => new { s.SetId, s.CreatedAt })
                                         .ToListAsync(ct);
        if (setStates.Count == 0)
            return;

        var setIds = setStates.Select(s => s.SetId).ToList();
        var members = await context.WordSetMembers.AsNoTracking()
                                   .Where(m => setIds.Contains(m.SetId))
                                   .Select(m => new { m.SetId, m.WordId, m.ReadingIndex })
                                   .ToListAsync(ct);

        var setDates = setStates.ToDictionary(s => s.SetId, s => DateOnly.FromDateTime(s.CreatedAt));

        // A held set is mastered knowledge from the day it was taken, so it counts as mature from that date on;
        // the pair's own earlier history survives, clipped by MergePairSegments. Pairs with a direct card are
        // excluded entirely, mirroring the NOT EXISTS in CoverageComputeService.
        foreach (var member in members)
        {
            var pair = (member.WordId, (byte)member.ReadingIndex);
            if (directCardPairs.Contains(pair)) continue;

            if (!byPair.TryGetValue(pair, out var segments))
                byPair[pair] = segments = new List<KnownSegment>(1);

            segments.Add(new KnownSegment(setDates[member.SetId], null, true));
        }
    }

    /// <summary>
    /// First review and first maturity crossing per card. Both are immutable once set, so this is cached for a
    /// week; the walk that produces it is the only place the full review log is read.
    /// </summary>
    private async Task<Dictionary<long, TransitionDates>> GetTransitionDatesAsync(string userId, CancellationToken ct)
    {
        var cacheKey = $"srsdates:{userId}";

        if (memoryCache.TryGetValue(cacheKey, out Dictionary<long, TransitionDates>? l1) && l1 != null)
            return l1;

        var cached = await ReadTransitionDatesAsync(cacheKey);
        if (cached != null)
        {
            memoryCache.Set(cacheKey, cached, SegmentsL1Ttl);
            return cached;
        }

        var dates = await ComputeTransitionDatesAsync(userId, ct);
        await WriteTransitionDatesAsync(cacheKey, dates);
        memoryCache.Set(cacheKey, dates, SegmentsL1Ttl);
        return dates;
    }

    /// <summary>
    /// Streamed in one ordered pass, counting as it goes: a separate COUNT to decide whether the walk is
    /// affordable would itself be a full scan of the rows it is guarding against.
    /// </summary>
    private async Task<Dictionary<long, TransitionDates>> ComputeTransitionDatesAsync(string userId, CancellationToken ct)
    {
        var logs = (from log in userContext.FsrsReviewLogs.AsNoTracking()
                    join card in userContext.FsrsCards.AsNoTracking() on log.CardId equals card.CardId
                    where card.UserId == userId
                    orderby log.CardId, log.ReviewDateTime
                    select new { log.CardId, log.ReviewDateTime })
            .AsAsyncEnumerable();

        var dates = new Dictionary<long, TransitionDates>();
        long currentCard = -1;
        DateTime firstReview = default;
        DateTime previous = default;
        DateOnly? matured = null;
        var seen = 0L;
        var overrun = false;

        await foreach (var log in logs.WithCancellation(ct))
        {
            // Cards past the cap get no cached dates and fall back to their last review, which is what
            // an uncached card does anyway.
            if (++seen > MaxWalkableReviews)
            {
                overrun = true;
                break;
            }

            if (log.CardId != currentCard)
            {
                Flush();
                currentCard = log.CardId;
                firstReview = log.ReviewDateTime;
                previous = log.ReviewDateTime;
                matured = null;
                continue;
            }

            // The gap to the next review stands in for the interval the scheduler had assigned.
            if (matured is null && log.ReviewDateTime - previous >= MatureInterval)
                matured = DateOnly.FromDateTime(previous);

            previous = log.ReviewDateTime;
        }

        Flush();

        if (overrun)
            logger.LogWarning("Stopped the maturity walk for {UserId} at {ReviewCount} reviews", userId, MaxWalkableReviews);

        return dates;

        void Flush()
        {
            if (currentCard >= 0)
                dates[currentCard] = new TransitionDates(DateOnly.FromDateTime(firstReview), matured);
        }
    }

    /// <summary>
    /// A journey whose last point disagrees with the coverage number displayed beside it reads as broken;
    /// the two are computed by different code paths, so drift is worth surfacing.
    /// </summary>
    private async Task WarnOnCoverageDriftAsync(string userId, int deckId, JourneyDto journey)
    {
        if (journey.Points.Count == 0)
            return;

        try
        {
            var coverage = await UserCoverageChunkHelper.GetCoverage(userContext, userId, [deckId]);
            if (!coverage.MatureCoverage.TryGetValue(deckId, out var stored))
                return;

            var delta = Math.Abs(stored - journey.CurrentCoverage);
            if (delta > ParityTolerancePoints)
            {
                logger.LogWarning(
                    "Coverage journey endpoint {Journey:F2}% disagrees with stored coverage {Stored:F2}% for deck {DeckId}",
                    journey.CurrentCoverage, stored, deckId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Coverage parity check failed for deck {DeckId}", deckId);
        }
    }

    private void LogIfSlow(Stopwatch started, int? deckId)
    {
        started.Stop();
        if (started.Elapsed > ColdComputeWarningThreshold)
        {
            logger.LogWarning("Coverage journey cold computation took {ElapsedMs} ms (deck {DeckId})",
                              started.ElapsedMilliseconds, deckId);
        }
    }

    private async Task<DateTime?> GetCoverageStampAsync(string userId, CancellationToken ct) =>
        await userContext.UserMetadatas.AsNoTracking()
                         .Where(m => m.UserId == userId)
                         .Select(m => m.CoverageRefreshedAt)
                         .FirstOrDefaultAsync(ct);

    private static string StampKey(DateTime? stamp) => stamp?.Ticks.ToString() ?? "nostamp";

    private static TimeSpan TtlFor(DateTime? stamp) => stamp != null ? CacheTtl : UnstampedCacheTtl;

    private async Task<T?> ReadJsonAsync<T>(string key) where T : class
    {
        try
        {
            var cached = await redis.GetDatabase().StringGetAsync(key);
            if (!cached.IsNullOrEmpty)
                return JsonSerializer.Deserialize<T>((string)cached!);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed reading journey cache {CacheKey}", key);
        }

        return null;
    }

    private async Task WriteJsonAsync<T>(string key, T value, TimeSpan ttl)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(value), ttl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed writing journey cache {CacheKey}", key);
        }
    }

    private async Task<Dictionary<long, TransitionDates>?> ReadTransitionDatesAsync(string key)
    {
        try
        {
            var cached = await redis.GetDatabase().StringGetAsync(key);
            if (cached.IsNullOrEmpty)
                return null;

            var flat = MessagePackSerializer.Deserialize<long[]>((byte[])cached!, MapOptions);
            var dates = new Dictionary<long, TransitionDates>(flat.Length / 3);
            for (var i = 0; i + 2 < flat.Length; i += 3)
            {
                var matured = flat[i + 2] < 0 ? (DateOnly?)null : DateOnly.FromDayNumber((int)flat[i + 2]);
                dates[flat[i]] = new TransitionDates(DateOnly.FromDayNumber((int)flat[i + 1]), matured);
            }

            return dates;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed reading transition dates {CacheKey}", key);
            return null;
        }
    }

    private async Task WriteTransitionDatesAsync(string key, Dictionary<long, TransitionDates> dates)
    {
        try
        {
            var flat = new long[dates.Count * 3];
            var i = 0;
            foreach (var (cardId, value) in dates)
            {
                flat[i++] = cardId;
                flat[i++] = value.FirstReview.DayNumber;
                flat[i++] = value.Matured?.DayNumber ?? -1;
            }

            await redis.GetDatabase().StringSetAsync(key, MessagePackSerializer.Serialize(flat, MapOptions), TransitionDatesTtl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed writing transition dates {CacheKey}", key);
        }
    }
}
