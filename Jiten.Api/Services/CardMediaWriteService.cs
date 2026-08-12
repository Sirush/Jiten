using Jiten.Core;
using Jiten.Core.Data.User;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public enum CardMediaWriteStatus
{
    Ok,
    Invalid,
    TooLarge,
    QuotaExceeded,
    Conflict
}

/// <param name="UsedBytes">The user's total stored bytes after this write, so a caller writing several
/// files in one request can keep accounting without re-querying between them.</param>
public sealed record CardMediaWriteResult(
    CardMediaWriteStatus Status,
    UserCardMedia? Row,
    CardMediaKind? Kind,
    long StoredBytes,
    long UsedBytes);

public interface ICardMediaWriteService
{
    Task<CardMediaWriteResult> WriteAsync(string userId, int wordId, byte readingIndex, byte[] bytes,
                                          bool overwrite, CardMediaQuota quota, long usedBytes,
                                          CancellationToken ct = default);
}

/// <summary>
/// Turns uploaded bytes into stored card media: sniff, normalize, quota, row upsert,
/// CDN write, orphan cleanup
/// </summary>
public sealed class CardMediaWriteService(
    UserDbContext userContext,
    ICdnService cdn,
    ILogger<CardMediaWriteService> logger) : ICardMediaWriteService
{
    public const long MaxFileBytes = 5L * 1024 * 1024;

    public async Task<CardMediaWriteResult> WriteAsync(string userId, int wordId, byte readingIndex, byte[] bytes,
                                                       bool overwrite, CardMediaQuota quota, long usedBytes,
                                                       CancellationToken ct = default)
    {
        if (bytes.Length == 0)
            return new CardMediaWriteResult(CardMediaWriteStatus.Invalid, null, null, 0, usedBytes);

        if (bytes.Length > MaxFileBytes)
            return new CardMediaWriteResult(CardMediaWriteStatus.TooLarge, null, null, 0, usedBytes);

        var sniff = CardMediaSniffer.Detect(bytes);
        if (sniff is null)
            return new CardMediaWriteResult(CardMediaWriteStatus.Invalid, null, null, 0, usedBytes);

        // Normalize images (downscale to 1600px, strip metadata, re-encode to WebP; GIFs pass through). The
        // caller's size gate applies to the uploaded file; quota accounting below uses the processed size.
        var processed = CardMediaImageProcessor.Normalize(sniff.Kind, sniff.Extension, sniff.ContentType, bytes, logger);
        bytes = processed.Bytes;

        var existing = await userContext.UserCardMedia
                                        .FirstOrDefaultAsync(m => m.UserId == userId && m.WordId == wordId
                                                                  && m.ReadingIndex == readingIndex && m.Kind == sniff.Kind, ct);

        if (existing is not null && !overwrite)
            return new CardMediaWriteResult(CardMediaWriteStatus.Conflict, existing, sniff.Kind, 0, usedBytes);

        // Replacing the file already on this (word, reading, kind) frees its bytes, so a user sitting at
        // the ceiling can still swap an image for another of the same size.
        var newUsedBytes = usedBytes - (existing?.FileSizeBytes ?? 0) + bytes.Length;
        if (newUsedBytes > quota.MaxBytes)
            return new CardMediaWriteResult(CardMediaWriteStatus.QuotaExceeded, null, sniff.Kind, 0, usedBytes);

        var storagePath = CardMediaStorage.PathFor(userId, wordId, readingIndex, sniff.Kind, processed.Extension);
        var oldStoragePath = existing?.StoragePath;
        // Replacing the media makes any original the renormalize backfill retained unreachable too.
        var retainedOriginal = existing?.PreviousStoragePath;

        string? uploadedStoragePath = null;
        await using var transaction = await userContext.Database.BeginTransactionAsync(ct);
        try
        {
            if (existing is null)
            {
                existing = new UserCardMedia { UserId = userId, WordId = wordId, ReadingIndex = readingIndex, Kind = sniff.Kind };
                userContext.UserCardMedia.Add(existing);
            }

            existing.StoragePath = storagePath;
            existing.ContentType = processed.ContentType;
            existing.FileSizeBytes = bytes.Length;
            existing.CreatedAt = DateTime.UtcNow;
            existing.PreviousStoragePath = null;
            existing.PreviousContentType = null;
            existing.PreviousFileSizeBytes = null;
            await userContext.SaveChangesAsync(ct);

            await cdn.UploadFile(bytes, storagePath, secure: true);
            uploadedStoragePath = storagePath;

            await transaction.CommitAsync(ct);
        }
        catch (Exception) when (uploadedStoragePath != null)
        {
            logger.LogError("Failed to commit card-media upload, cleaning up CDN file {Path}", uploadedStoragePath);
            try { await cdn.DeleteFile(uploadedStoragePath, secure: true); }
            catch { /* best effort */ }
            throw;
        }

        // Once the row is durably committed the files it replaced are orphaned; removing them is best-effort.
        foreach (var path in new[] { oldStoragePath, retainedOriginal }.Where(p => p != null && p != storagePath))
        {
            try { await cdn.DeleteFile(path!, secure: true); }
            catch (Exception ex) { logger.LogWarning(ex, "Failed to delete card-media CDN file {Path}", path); }
        }

        return new CardMediaWriteResult(CardMediaWriteStatus.Ok, existing, sniff.Kind, bytes.Length, newUsedBytes);
    }
}
