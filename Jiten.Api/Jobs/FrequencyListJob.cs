using Hangfire;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

/// <summary>
/// Builds a custom frequency list's Yomitan zip + CSV from a filtered/hand-picked deck set and uploads them
/// to the CDN. Runs on the default queue. Also owns the monthly auto-update regeneration and the daily
/// transient-cleanup recurring jobs.
/// </summary>
public class FrequencyListJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IDbContextFactory<UserDbContext> userContextFactory,
    ICdnService cdn,
    IBackgroundJobClient backgroundJobs,
    IConfiguration configuration,
    ILogger<FrequencyListJob> logger)
{
    private static readonly object GateLock = new();
    private static readonly HashSet<long> Generating = new();

    private static readonly TimeSpan TransientLifetime = TimeSpan.FromHours(48);

    public static string ZipStoragePath(string userId, long listId) => $"freq-lists/{userId}/{listId}.zip";
    public static string CsvStoragePath(string userId, long listId) => $"freq-lists/{userId}/{listId}.csv";

    [Queue("default")]
    [AutomaticRetry(Attempts = 1)]
    public async Task Generate(long listId)
    {
        lock (GateLock)
        {
            if (!Generating.Add(listId))
            {
                logger.LogInformation("FrequencyListJob: {ListId} is already generating, skipping", listId);
                return;
            }
        }

        try
        {
            await GenerateInternal(listId);
        }
        finally
        {
            lock (GateLock)
                Generating.Remove(listId);
        }
    }

    private async Task GenerateInternal(long listId)
    {
        await using var userContext = await userContextFactory.CreateDbContextAsync();

        var list = await userContext.UserFrequencyLists.FirstOrDefaultAsync(f => f.Id == listId);
        if (list is null)
        {
            logger.LogWarning("FrequencyListJob: list {ListId} no longer exists", listId);
            return;
        }

        list.Status = FrequencyListStatus.Generating;
        await userContext.SaveChangesAsync();

        try
        {
            var definition = list.Definition;

            List<int> deckIds;
            await using (var jitenContext = await contextFactory.CreateDbContextAsync())
            {
                deckIds = await DeckFilterHelper.ResolveDeckIdsAsync(jitenContext, definition, list.Mode);
            }

            if (deckIds.Count == 0)
            {
                list.Status = FrequencyListStatus.Failed;
                await userContext.SaveChangesAsync();
                logger.LogWarning("FrequencyListJob: list {ListId} matched no decks; marked Failed", listId);
                return;
            }

            // Observed-only variant: skips the full-vocabulary initialisation the global pipeline needs and
            // returns the observed words' WordForms so both generators share one fetch.
            var (wordFrequencies, formFrequencies, forms) =
                await JitenHelper.ComputeObservedFrequenciesForDeckIds(contextFactory, deckIds);

            // Saved lists carry a PublicSlug (minted when the list became permanent); the slug URLs make
            // the Yomitan dictionary updatable, with the index endpoint serving the fresh revision straight
            // from the DB (the CDN pull-zone cache is not purged on overwrite, so it can't be trusted for
            // update checks). Transient lists expire after 48h, so they ship non-updatable.
            var generatedAt = DateTime.UtcNow;
            string? indexUrl = null, downloadUrl = null;
            if (!string.IsNullOrEmpty(list.PublicSlug))
            {
                indexUrl = FrequencyListLinks.IndexUrl(configuration, list.PublicSlug);
                downloadUrl = FrequencyListLinks.DownloadUrl(configuration, list.PublicSlug);
            }

            string indexJson = YomitanHelper.GetCustomFrequencyIndexJson(list.Name, generatedAt, indexUrl, downloadUrl);
            var zipBytes = await YomitanHelper.GenerateYomitanFrequencyDeck(contextFactory, wordFrequencies, formFrequencies,
                                                                            null, indexJson, forms);
            var csvBytes = await YomitanHelper.GenerateFrequencyCsv(contextFactory, wordFrequencies, formFrequencies, forms);

            var zipUrl = await cdn.UploadFile(zipBytes, ZipStoragePath(list.UserId, list.Id));
            var csvUrl = await cdn.UploadFile(csvBytes, CsvStoragePath(list.UserId, list.Id));

            list.ZipUrl = zipUrl;
            list.CsvUrl = csvUrl;
            list.DeckCount = deckIds.Count;
            list.WordCount = wordFrequencies.Count(w => w.UsedInMediaAmount > 0);
            list.Status = FrequencyListStatus.Ready;
            list.GeneratedAt = generatedAt;

            // Only saved lists can back a study deck, and transient files are dropped after 48h anyway.
            if (list.IsSaved)
            {
                list.RankedWordsBlob = FrequencyListBlobPacker.Pack(BuildRankedWords(formFrequencies));
                list.BlobGeneratedAt = generatedAt;
            }

            await userContext.SaveChangesAsync();

            logger.LogInformation("FrequencyListJob: list {ListId} ready ({Decks} decks, {Words} words)",
                                  listId, list.DeckCount, list.WordCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FrequencyListJob: generation FAILED for list {ListId}", listId);
            list.Status = FrequencyListStatus.Failed;
            await userContext.SaveChangesAsync();
            throw;
        }
    }

    /// <summary>
    /// Enqueued at the end of ComputationJob.RecomputeFrequencies: regenerate every saved list that opted
    /// into auto-update, one enqueue per list, so custom lists refresh together with the official ones.
    /// </summary>
    [Queue("default")]
    public async Task RegenerateAutoUpdateLists()
    {
        await using var userContext = await userContextFactory.CreateDbContextAsync();

        var ids = await userContext.UserFrequencyLists
                                   .Where(f => f.IsSaved && f.AutoUpdate)
                                   .Select(f => f.Id)
                                   .ToListAsync();

        foreach (var id in ids)
        {
            try
            {
                backgroundJobs.Enqueue<FrequencyListJob>(j => j.Generate(id));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "FrequencyListJob: failed to enqueue auto-update for list {ListId}", id);
            }
        }

        logger.LogInformation("FrequencyListJob: enqueued {Count} auto-update regenerations", ids.Count);
    }

    /// <summary>
    /// Daily: expire transient (unsaved) lists whose generated files are older than 48h. The row and its
    /// filter definition are KEPT (Status → Expired) so the user can regenerate in one click; only the CDN
    /// files are removed and the urls cleared. Measured from GeneratedAt so a regenerate resets the window.
    /// </summary>
    [Queue("default")]
    public async Task CleanupTransientLists()
    {
        await using var userContext = await userContextFactory.CreateDbContextAsync();

        var cutoff = DateTime.UtcNow - TransientLifetime;
        // Age from GeneratedAt (falling back to CreatedAt) so a regenerate resets the 48h window; already
        // Expired rows are excluded by the Ready filter, so the job is idempotent across daily runs.
        var stale = await userContext.UserFrequencyLists
                                     .Where(f => !f.IsSaved
                                                 && f.Status == FrequencyListStatus.Ready
                                                 && (f.GeneratedAt ?? f.CreatedAt) < cutoff)
                                     .ToListAsync();

        foreach (var list in stale)
        {
            await DeleteCdnFiles(list);
            list.ZipUrl = null;
            list.CsvUrl = null;
            list.Status = FrequencyListStatus.Expired;
        }

        await userContext.SaveChangesAsync();
        logger.LogInformation("FrequencyListJob: expired {Count} transient lists", stale.Count);
    }

    private static List<(int WordId, byte ReadingIndex)> BuildRankedWords(List<JmDictWordFormFrequency> formFrequencies)
    {
        return formFrequencies
               .Where(f => f.UsedInMediaAmount > 0 && f.ReadingIndex is >= 0 and <= byte.MaxValue)
               .OrderBy(f => f.FrequencyRank)
               .ThenBy(f => f.WordId)
               .ThenBy(f => f.ReadingIndex)
               .Select(f => (f.WordId, (byte)f.ReadingIndex))
               .ToList();
    }

    private async Task DeleteCdnFiles(UserFrequencyList list)
    {
        try
        {
            if (!string.IsNullOrEmpty(list.ZipUrl))
                await cdn.DeleteFile(ZipStoragePath(list.UserId, list.Id));
            if (!string.IsNullOrEmpty(list.CsvUrl))
                await cdn.DeleteFile(CsvStoragePath(list.UserId, list.Id));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FrequencyListJob: failed to delete CDN files for list {ListId}", list.Id);
        }
    }
}
