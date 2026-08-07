using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>An archived review. PseudoCardId is negative, so it never collides with a live FsrsCard.CardId.</summary>
public readonly record struct ArchivedReview(long PseudoCardId, DateTime ReviewUtc, FsrsRating Rating, int? DurationMs);

/// <summary>A review from either source. CardId is negative for one that came out of the archive.</summary>
public readonly record struct UserReview(long CardId, DateTime ReviewUtc, FsrsRating Rating, int? DurationMs);

/// <summary>
/// Writes removed cards to <see cref="FsrsCardArchive"/>
/// </summary>
public static class CardArchiveService
{
    private const int BulkChunkSize = 2000;

    public static async Task<int> ArchiveCardsAsync(
        UserDbContext ctx, string userId, IReadOnlyList<FsrsCard> cards,
        CardArchiveReason reason, Func<FsrsCard, byte?>? coveringIndex = null)
    {
        if (cards.Count == 0)
            return 0;

        var cardIds = cards.Where(c => c.CardId > 0).Select(c => c.CardId).ToList();

        var logsByCard = cardIds.Count == 0
            ? new Dictionary<long, List<FsrsReviewLog>>()
            : (await ctx.FsrsReviewLogs
                        .AsNoTracking()
                        .Where(l => cardIds.Contains(l.CardId))
                        .ToListAsync())
              .GroupBy(l => l.CardId)
              .ToDictionary(g => g.Key, g => g.ToList());

        var entries = cards.Select(c => (Card: c, Reviews: (IReadOnlyList<PackedReview>)
                                            (logsByCard.GetValueOrDefault(c.CardId) ?? [])
                                            .Select(l => new PackedReview(l.Rating, l.ReviewDateTime, l.ReviewDuration))
                                            .ToList()))
                           .ToList();

        return await WriteAsync(ctx, userId, entries, reason, coveringIndex);
    }

    /// <summary>
    /// Archives cards an importer resolved not to insert
    /// Entries with no reviews are ignored
    /// </summary>
    public static Task<int> ArchiveUninsertedCardsAsync(
        UserDbContext ctx, string userId,
        IReadOnlyList<(FsrsCard Card, IReadOnlyList<PackedReview> Reviews)> entries,
        CardArchiveReason reason, Func<FsrsCard, byte?>? coveringIndex = null)
    {
        var withHistory = entries.Where(e => e.Reviews.Count > 0).ToList();
        return withHistory.Count == 0
            ? Task.FromResult(0)
            : WriteAsync(ctx, userId, withHistory, reason, coveringIndex);
    }

    private static async Task<int> WriteAsync(
        UserDbContext ctx, string userId, IReadOnlyList<(FsrsCard Card, IReadOnlyList<PackedReview> Reviews)> entries,
        CardArchiveReason reason, Func<FsrsCard, byte?>? coveringIndex)
    {
        var archivedAt = DateTime.UtcNow;
        var existing = await LoadExistingAsync(ctx, userId, entries.Select(e => (e.Card.WordId, e.Card.ReadingIndex)));

        foreach (var (card, logs) in entries)
        {
            // The unique key permits only one row per form, so a re-archived form unions its history into the
            // row already there rather than starting a second one.
            if (existing.TryGetValue((card.WordId, card.ReadingIndex), out var row))
                MergeInto(row, logs, 0);
            else
            {
                var packed = ReviewLogPacker.Pack(logs);
                row = new FsrsCardArchive
                      {
                          UserId = userId,
                          WordId = card.WordId,
                          ReadingIndex = card.ReadingIndex,
                          ReviewCount = packed.ReviewCount,
                          FirstReview = packed.FirstReview,
                          HistoryTruncated = packed.Truncated,
                          Logs = packed.Logs
                      };
                await ctx.FsrsCardArchives.AddAsync(row);
                existing[(card.WordId, card.ReadingIndex)] = row;
            }

            row.ArchivedAt = archivedAt;
            row.Reason = reason;
            row.CoveringReadingIndex = coveringIndex?.Invoke(card);
            row.State = card.State;
            row.Step = card.Step;
            row.Stability = card.Stability;
            row.Difficulty = card.Difficulty;
            row.Due = card.Due;
            row.LastReview = card.LastReview;
            row.Lapses = card.Lapses;
            row.CardCreatedAt = card.CreatedAt;
        }

        return entries.Count;
    }

    /// <summary>
    /// Archives everything the query matches, in chunks, saving each chunk. Call before an
    /// <c>ExecuteDeleteAsync</c> on the same query, inside a transaction, since the delete bypasses the
    /// change tracker and the per-chunk saves are only atomic with it under one.
    /// </summary>
    public static async Task<int> ArchiveByQueryAsync(
        UserDbContext ctx, string userId, IQueryable<FsrsCard> query, CardArchiveReason reason)
    {
        var archived = 0;
        var cursor = 0L;

        while (true)
        {
            var cards = await query.AsNoTracking()
                                   .Where(c => c.CardId > cursor)
                                   .OrderBy(c => c.CardId)
                                   .Take(BulkChunkSize)
                                   .ToListAsync();

            if (cards.Count == 0)
                break;

            archived += await ArchiveCardsAsync(ctx, userId, cards, reason);
            // Flushed per chunk, or every chunk's archive rows and unpacked histories pile up in the change
            // tracker at once. The caller's transaction still makes the whole run atomic.
            await ctx.SaveChangesAsync();

            cursor = cards[^1].CardId;
        }

        return archived;
    }

    /// <summary>
    /// Folds <paramref name="source"/> into <paramref name="target"/> so a word merge that lands two archive
    /// rows on the same form leaves one. The caller deletes <paramref name="source"/>.
    /// </summary>
    public static void MergeArchiveRows(FsrsCardArchive target, FsrsCardArchive source)
    {
        var (reviews, corrupt) = ReadReviews(source);
        MergeInto(target, reviews, Math.Max(0, source.ReviewCount - reviews.Count), corrupt);

        if (source.ArchivedAt <= target.ArchivedAt)
            return;

        target.ArchivedAt = source.ArchivedAt;
        target.Reason = source.Reason;
        target.CoveringReadingIndex = source.CoveringReadingIndex;
        target.State = source.State;
        target.Step = source.Step;
        target.Stability = source.Stability;
        target.Difficulty = source.Difficulty;
        target.Due = source.Due;
        target.LastReview = source.LastReview;
        target.Lapses = source.Lapses;
        target.CardCreatedAt = source.CardCreatedAt;
    }

    private static void MergeInto(FsrsCardArchive row, IEnumerable<PackedReview> incoming, int incomingMissing,
                                  bool incomingCorrupt = false)
    {
        var incomingList = incoming as IReadOnlyCollection<PackedReview> ?? incoming.ToList();
        var (existingReviews, corrupt) = ReadReviews(row);

        var missing = Math.Max(0, row.ReviewCount - existingReviews.Count) + incomingMissing;
        var union = DistinctBySecond(existingReviews.Concat(incomingList)).ToList();

        if (union.Count > DistinctBySecond(incomingList).Count() || missing > 0)
            row.HistoryMerged = true;

        var packed = ReviewLogPacker.Pack(union, markTruncated: missing > 0 || corrupt || incomingCorrupt);

        row.Logs = packed.Logs;
        row.FirstReview = packed.FirstReview;
        row.HistoryTruncated = packed.Truncated;
        row.ReviewCount = packed.ReviewCount + missing;
    }

    /// <summary>
    /// Every archived review for a user, each carrying a negative pseudo-card id derived from its archive row.
    /// </summary>
    public static async Task<List<ArchivedReview>> LoadArchivedReviewsAsync(UserDbContext ctx, string userId)
    {
        var rows = await ctx.FsrsCardArchives
                            .AsNoTracking()
                            .Where(a => a.UserId == userId && a.ReviewCount > 0 && a.Logs != null && a.FirstReview != null)
                            .Select(a => new { a.ArchiveId, a.FirstReview, a.Logs })
                            .ToListAsync();

        var result = new List<ArchivedReview>();

        foreach (var row in rows)
        {
            var (reviews, _) = ReadReviews(row.Logs, row.FirstReview);
            foreach (var review in reviews)
                result.Add(new ArchivedReview(-row.ArchiveId, review.ReviewDateTime, review.Rating, review.ReviewDuration));
        }

        return result;
    }

    /// <summary>
    /// A user's whole grading history
    /// </summary>
    public static async Task<List<UserReview>> LoadAllReviewsAsync(UserDbContext ctx, string userId)
    {
        var live = await ctx.FsrsReviewLogs
                            .AsNoTracking()
                            .Where(l => l.Card.UserId == userId)
                            .Select(l => new { l.CardId, l.ReviewDateTime, l.Rating, l.ReviewDuration })
                            .ToListAsync();

        var archived = await LoadArchivedReviewsAsync(ctx, userId);

        var all = new List<UserReview>(live.Count + archived.Count);
        foreach (var l in live)
            all.Add(new UserReview(l.CardId, l.ReviewDateTime, l.Rating, l.ReviewDuration));
        foreach (var a in archived)
            all.Add(new UserReview(a.PseudoCardId, a.ReviewUtc, a.Rating, a.DurationMs));

        all.Sort(static (x, y) => x.CardId != y.CardId
                     ? x.CardId.CompareTo(y.CardId)
                     : x.ReviewUtc.CompareTo(y.ReviewUtc));

        return all;
    }

    /// <summary>
    /// Every local date the user has reviewed on, live and archived
    /// </summary>
    public static async Task<HashSet<DateOnly>> LoadAllReviewDatesAsync(UserDbContext ctx, string userId, TimeZoneInfo? timezone)
    {
        var dates = await ctx.FsrsReviewLogs
                             .AsNoTracking()
                             .Where(l => l.Card.UserId == userId)
                             .Select(l => l.ReviewDateTime)
                             .ToListAsync();

        var result = new HashSet<DateOnly>();
        foreach (var utc in dates)
            result.Add(ReviewRollupHelper.LocalDateOf(utc, timezone));

        foreach (var review in await LoadArchivedReviewsAsync(ctx, userId))
            result.Add(ReviewRollupHelper.LocalDateOf(review.ReviewUtc, timezone));

        return result;
    }

    /// <summary>
    /// Drops from archive rows the reviews now held live on the given cards, matched by second-truncated
    /// timestamp
    /// </summary>
    public static async Task<int> DropReviewsHeldLiveAsync(
        UserDbContext ctx, string userId, IReadOnlyList<FsrsCard> cards,
        Func<FsrsCard, IEnumerable<DateTime>> liveReviewTimes)
    {
        if (cards.Count == 0)
            return 0;

        var byKey = new Dictionary<(int WordId, byte ReadingIndex), FsrsCard>();
        foreach (var card in cards)
            byKey.TryAdd((card.WordId, card.ReadingIndex), card);
        var rows = await LoadExistingAsync(ctx, userId, byKey.Keys);

        var changed = 0;

        foreach (var (key, row) in rows)
        {
            if (ctx.Entry(row).State == EntityState.Deleted)
                continue;

            var (reviews, corrupt) = ReadReviews(row);
            if (corrupt || reviews.Count == 0)
                continue;

            var live = byKey[key];
            var liveTimes = liveReviewTimes(live).Select(TruncateToSecond).ToHashSet();
            if (liveTimes.Count == 0)
                continue;

            var kept = reviews.Where(r => !liveTimes.Contains(TruncateToSecond(r.ReviewDateTime))).ToList();
            if (kept.Count == reviews.Count)
                continue;

            var missing = Math.Max(0, row.ReviewCount - reviews.Count);
            var packed = ReviewLogPacker.Pack(kept, markTruncated: missing > 0 || row.HistoryTruncated);

            row.Logs = packed.Logs;
            row.FirstReview = packed.FirstReview;
            row.HistoryTruncated = packed.Truncated;
            row.ReviewCount = packed.ReviewCount + missing;
            changed++;
        }

        return changed;
    }

    /// <summary>Unpacks a row's history, reporting corruption rather than passing it off as an empty history.</summary>
    public static (List<PackedReview> Reviews, bool Corrupt) ReadReviews(FsrsCardArchive row)
        => ReadReviews(row.Logs, row.FirstReview);

    private static (List<PackedReview> Reviews, bool Corrupt) ReadReviews(byte[]? logs, DateTime? firstReview)
    {
        if (logs is not { Length: > 0 } || !firstReview.HasValue)
            return ([], false);

        try
        {
            return (ReviewLogPacker.Unpack(logs, firstReview.Value), false);
        }
        catch (InvalidDataException)
        {
            return ([], true);
        }
    }

    private static async Task<Dictionary<(int WordId, byte ReadingIndex), FsrsCardArchive>> LoadExistingAsync(
        UserDbContext ctx, string userId, IEnumerable<(int WordId, byte ReadingIndex)> keys)
    {
        var keySet = keys.ToHashSet();
        if (keySet.Count == 0)
            return new Dictionary<(int, byte), FsrsCardArchive>();

        var wordIds = keySet.Select(k => k.WordId).Distinct().ToList();
        var rows = await ctx.FsrsCardArchives
                            .Where(a => a.UserId == userId && wordIds.Contains(a.WordId))
                            .ToListAsync();

        var result = rows.Where(a => keySet.Contains((a.WordId, a.ReadingIndex)))
                         .ToDictionary(a => (a.WordId, a.ReadingIndex));

        // Rows added earlier in the same unit of work are not in the database yet; missing them here would
        // produce a second row for the form and violate the unique index at save time.
        foreach (var local in ctx.FsrsCardArchives.Local)
            if (local.UserId == userId && keySet.Contains((local.WordId, local.ReadingIndex))
                && ctx.Entry(local).State == EntityState.Added)
                result.TryAdd((local.WordId, local.ReadingIndex), local);

        return result;
    }

    /// <summary>Second precision is the resolution shared by Anki exports, the packed blob and Jiten's own logs.</summary>
    public static DateTime TruncateToSecond(DateTime value)
        => new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);

    /// <summary>
    /// Drops reviews that collapse onto a timestamp already seen
    /// </summary>
    public static IEnumerable<PackedReview> DistinctBySecond(IEnumerable<PackedReview> reviews, HashSet<DateTime>? seen = null)
    {
        seen ??= [];
        foreach (var review in reviews)
            if (seen.Add(TruncateToSecond(review.ReviewDateTime)))
                yield return review;
    }
}
