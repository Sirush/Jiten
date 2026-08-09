using Hangfire;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

/// <summary>
/// Re-runs upload-time normalization over card-media images that were stored unprocessed, which happens
/// whenever <see cref="CardMediaImageProcessor.Normalize"/> falls through to the original file. The rewrite
/// is additive: the normalized file lands on a new storage path and the original stays on the CDN, recorded
/// in the row's Previous* columns so the change can be reversed until it is explicitly discarded.
/// </summary>
public class CardMediaRenormalizeJob(
    IDbContextFactory<UserDbContext> userContextFactory,
    ICdnService cdn,
    ILogger<CardMediaRenormalizeJob> logger)
{
    public const string WebpContentType = "image/webp";

    private const int BatchSize = 100;

    public sealed record Stats(
        int Scanned, int Rewritten, int AlreadyOptimal, int Missing, int Changed, int Failed,
        long BytesBefore, long BytesAfter)
    {
        public static readonly Stats Empty = new(0, 0, 0, 0, 0, 0, 0, 0);
    }

    /// <summary>Rows the backfill would touch: images whose stored file never became WebP.</summary>
    public static IQueryable<UserCardMedia> Candidates(UserDbContext ctx) =>
        ctx.UserCardMedia.Where(m => m.Kind == CardMediaKind.Image && m.ContentType != WebpContentType);

    /// <summary>Rows still holding a superseded original, which are the reversible ones.</summary>
    public static IQueryable<UserCardMedia> Retained(UserDbContext ctx) =>
        ctx.UserCardMedia.Where(m => m.PreviousStoragePath != null);

    /// <param name="dryRun">Measures the identical work and writes nothing, so the reported saving is real.</param>
    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task RenormalizeAll(bool dryRun)
    {
        var stats = Stats.Empty;
        long lastId = 0;

        while (true)
        {
            await using var ctx = await userContextFactory.CreateDbContextAsync();

            var batch = await Candidates(ctx).AsNoTracking()
                                             .Where(m => m.Id > lastId)
                                             .OrderBy(m => m.Id)
                                             .Take(BatchSize)
                                             .ToListAsync();

            if (batch.Count == 0)
                break;

            foreach (var row in batch)
            {
                lastId = row.Id;
                stats = await ProcessAsync(ctx, row, dryRun, stats);
            }
        }

        logger.LogInformation(
            "Card-media renormalize ({Mode}) finished: {Scanned} scanned, {Rewritten} rewritten, "
            + "{AlreadyOptimal} already optimal, {Missing} missing from CDN, {Changed} changed mid-run, "
            + "{Failed} failed. {Before} -> {After} bytes.",
            dryRun ? "dry run" : "live", stats.Scanned, stats.Rewritten, stats.AlreadyOptimal, stats.Missing,
            stats.Changed, stats.Failed, stats.BytesBefore, stats.BytesAfter);
    }

    private async Task<Stats> ProcessAsync(UserDbContext ctx, UserCardMedia row, bool dryRun, Stats stats)
    {
        stats = stats with { Scanned = stats.Scanned + 1 };

        var bytes = await cdn.DownloadFile(row.StoragePath, secure: true);
        if (bytes is null || bytes.Length == 0)
        {
            logger.LogWarning("Card-media {MediaId} has no file at {Path}; leaving the row untouched.",
                              row.Id, row.StoragePath);
            return stats with { Missing = stats.Missing + 1 };
        }

        // A size that disagrees with the row means the file was replaced after this batch was read; the
        // bytes in hand are not the ones the row describes, so they must not be rewritten under it.
        if (bytes.LongLength != row.FileSizeBytes)
            return stats with { Changed = stats.Changed + 1 };

        var sniff = CardMediaSniffer.Detect(bytes);
        if (sniff is null || sniff.Kind != CardMediaKind.Image)
        {
            logger.LogWarning("Card-media {MediaId} at {Path} is not a recognised image; leaving it untouched.",
                              row.Id, row.StoragePath);
            return stats with { Failed = stats.Failed + 1 };
        }

        var processed = CardMediaImageProcessor.Normalize(
            sniff.Kind, sniff.Extension, sniff.ContentType, bytes, logger);

        // Normalization still falling through, or an encode that saves nothing: either way the original
        // stays. Rewriting a file to something no smaller only spends storage and loses a generation.
        if (processed.ContentType != WebpContentType || processed.Bytes.Length == 0)
            return stats with { Failed = stats.Failed + 1 };

        if (processed.Bytes.LongLength >= bytes.LongLength)
            return stats with { AlreadyOptimal = stats.AlreadyOptimal + 1 };

        stats = stats with
        {
            BytesBefore = stats.BytesBefore + bytes.LongLength,
            BytesAfter = stats.BytesAfter + processed.Bytes.LongLength
        };

        if (dryRun)
            return stats with { Rewritten = stats.Rewritten + 1 };

        return await CommitAsync(ctx, row, processed, stats);
    }

    private async Task<Stats> CommitAsync(
        UserDbContext ctx, UserCardMedia row, CardMediaImageProcessor.Processed processed, Stats stats)
    {
        var newPath = CardMediaStorage.PathFor(row.UserId, row.WordId, row.ReadingIndex, row.Kind, processed.Extension);
        var uploaded = false;

        Stats Abandon(Stats current, bool failed) => current with
        {
            Failed = failed ? current.Failed + 1 : current.Failed,
            Changed = failed ? current.Changed : current.Changed + 1,
            BytesBefore = current.BytesBefore - row.FileSizeBytes,
            BytesAfter = current.BytesAfter - processed.Bytes.Length
        };

        try
        {
            await cdn.UploadFile(processed.Bytes, newPath, secure: true);
            uploaded = true;

            // Read back from the storage zone before anything points at it: an upload that reported success
            // but did not land would otherwise leave the card showing a broken file.
            var stored = await cdn.DownloadFile(newPath, secure: true);
            if (stored is null || stored.LongLength != processed.Bytes.LongLength)
            {
                logger.LogWarning("Card-media {MediaId}: re-encoded file at {Path} did not read back; "
                                  + "the row still points at the original.", row.Id, newPath);
                await TryDeleteAsync(newPath);
                return Abandon(stats, failed: true);
            }

            // The guard is the whole safety story for concurrent uploads: a user who replaced this file
            // between the read and now leaves the row no longer matching, and the rewrite is abandoned.
            // PreviousStoragePath being null keeps a second run from overwriting a retained original.
            var updated = await ctx.UserCardMedia
                                   .Where(m => m.Id == row.Id
                                               && m.StoragePath == row.StoragePath
                                               && m.FileSizeBytes == row.FileSizeBytes
                                               && m.PreviousStoragePath == null)
                                   .ExecuteUpdateAsync(s => s
                                                            .SetProperty(m => m.StoragePath, newPath)
                                                            .SetProperty(m => m.ContentType, WebpContentType)
                                                            .SetProperty(m => m.FileSizeBytes,
                                                                         (long)processed.Bytes.Length)
                                                            .SetProperty(m => m.PreviousStoragePath, row.StoragePath)
                                                            .SetProperty(m => m.PreviousContentType, row.ContentType)
                                                            .SetProperty(m => m.PreviousFileSizeBytes,
                                                                         (long?)row.FileSizeBytes));

            if (updated == 0)
            {
                await TryDeleteAsync(newPath);
                return Abandon(stats, failed: false);
            }

            return stats with { Rewritten = stats.Rewritten + 1 };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Card-media renormalize failed for {MediaId}; the row still points at {Path}.",
                            row.Id, row.StoragePath);

            // Nothing references the new file, since the row was never updated.
            if (uploaded)
                await TryDeleteAsync(newPath);

            return Abandon(stats, failed: true);
        }
    }

    /// <summary>
    /// Points every rewritten row back at its original and removes the file the backfill wrote. Safe at any
    /// time: the originals were never deleted, and a row a user has since replaced holds no original to restore.
    /// </summary>
    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task RollbackAll()
    {
        var restored = 0;
        var failed = 0;
        long lastId = 0;

        while (true)
        {
            await using var ctx = await userContextFactory.CreateDbContextAsync();

            var batch = await Retained(ctx).AsNoTracking()
                                           .Where(m => m.Id > lastId)
                                           .OrderBy(m => m.Id)
                                           .Take(BatchSize)
                                           .ToListAsync();

            if (batch.Count == 0)
                break;

            foreach (var row in batch)
            {
                lastId = row.Id;
                var rewritten = row.StoragePath;

                try
                {
                    var updated = await ctx.UserCardMedia
                                           .Where(m => m.Id == row.Id && m.StoragePath == rewritten)
                                           .ExecuteUpdateAsync(s => s
                                               .SetProperty(m => m.StoragePath, row.PreviousStoragePath!)
                                               .SetProperty(m => m.ContentType, row.PreviousContentType!)
                                               .SetProperty(m => m.FileSizeBytes, row.PreviousFileSizeBytes!.Value)
                                               .SetProperty(m => m.PreviousStoragePath, (string?)null)
                                               .SetProperty(m => m.PreviousContentType, (string?)null)
                                               .SetProperty(m => m.PreviousFileSizeBytes, (long?)null));

                    if (updated == 0)
                    {
                        failed++;
                        continue;
                    }

                    restored++;

                    // Only now is the re-encoded file unreferenced; leaving it would orphan it on the CDN.
                    await TryDeleteAsync(rewritten);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Card-media rollback failed for {MediaId}", row.Id);
                    failed++;
                }
            }
        }

        logger.LogInformation("Card-media renormalize rolled back: {Restored} restored, {Failed} skipped.",
                              restored, failed);
    }

    /// <summary>
    /// Deletes the originals superseded by a completed backfill. Irreversible: rollback needs those files.
    /// </summary>
    [Queue("default")]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task DiscardOriginals()
    {
        var discarded = 0;
        var skipped = 0;
        long lastId = 0;

        while (true)
        {
            await using var ctx = await userContextFactory.CreateDbContextAsync();

            var batch = await Retained(ctx).AsNoTracking()
                                           .Where(m => m.Id > lastId)
                                           .OrderBy(m => m.Id)
                                           .Take(BatchSize)
                                           .ToListAsync();

            if (batch.Count == 0)
                break;

            foreach (var row in batch)
            {
                lastId = row.Id;
                var original = row.PreviousStoragePath!;

                // Clearing the columns first means a delete that fails leaves a CDN orphan rather than a row
                // pointing at a file that is already gone.
                var cleared = await ctx.UserCardMedia
                                       .Where(m => m.Id == row.Id && m.PreviousStoragePath == original)
                                       .ExecuteUpdateAsync(s => s
                                           .SetProperty(m => m.PreviousStoragePath, (string?)null)
                                           .SetProperty(m => m.PreviousContentType, (string?)null)
                                           .SetProperty(m => m.PreviousFileSizeBytes, (long?)null));

                if (cleared == 0)
                {
                    skipped++;
                    continue;
                }

                await TryDeleteAsync(original);
                discarded++;
            }
        }

        logger.LogInformation("Card-media originals discarded: {Discarded} deleted, {Skipped} skipped.",
                              discarded, skipped);
    }

    private async Task TryDeleteAsync(string path)
    {
        try
        {
            await cdn.DeleteFile(path, secure: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up card-media file {Path}", path);
        }
    }
}
