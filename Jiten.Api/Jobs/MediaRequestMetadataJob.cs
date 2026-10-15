using Hangfire;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

/// <summary>
/// Reads a media request's link from its provider. Each entry point runs on that provider's single-worker queue and waits
/// out the same delay as the deck metadata jobs, so request lookups and deck lookups share one call budget per provider.
/// </summary>
public class MediaRequestMetadataJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IExternalMetadataResolver resolver,
    ILogger<MediaRequestMetadataJob> logger)
{
    [Queue("anilist")]
    public Task FetchAnilist(int requestId, string url) => Fetch(requestId, url, FetchMetadataJob.ANILIST_DELAY);

    [Queue("jikan")]
    public Task FetchJikan(int requestId, string url) => Fetch(requestId, url, FetchMetadataJob.JIKAN_DELAY);

    [Queue("vndb")]
    public Task FetchVndb(int requestId, string url) => Fetch(requestId, url, FetchMetadataJob.VNDB_DELAY);

    [Queue("tmdb")]
    public Task FetchTmdb(int requestId, string url) => Fetch(requestId, url, FetchMetadataJob.TMDB_DELAY);

    [Queue("igdb")]
    public Task FetchIgdb(int requestId, string url) => Fetch(requestId, url, FetchMetadataJob.IGDB_DELAY);

    [Queue("books")]
    public Task FetchBooks(int requestId, string url) => Fetch(requestId, url, FetchMetadataJob.GOOGLE_BOOKS_DELAY);

    /// <summary>The Syosetu source throttles its own calls.</summary>
    [Queue(WebNovelQueues.SyosetuMetadata)]
    public Task FetchSyosetu(int requestId, string url) => Fetch(requestId, url, 0);

    private async Task Fetch(int requestId, string url, float delaySeconds)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var request = await context.MediaRequests
                                   .Include(r => r.Genres)
                                   .Include(r => r.Tags)
                                   .Include(r => r.Titles)
                                   .Include(r => r.Links)
                                   .AsSplitQuery()
                                   .FirstOrDefaultAsync(r => r.Id == requestId);

        // A link edited after this job was queued queued its own job, so this one has nothing left to do.
        if (request == null || request.ExternalUrl != url || !ExternalMetadataResolver.TryGetProvider(url, out var provider))
            return;

        try
        {
            var metadata = await resolver.ResolveAsync(url, request.MediaType);

            if (!await context.MediaRequests.AnyAsync(r => r.Id == requestId && r.ExternalUrl == url))
                return;

            if (metadata == null)
            {
                request.MetadataStatus = MediaRequestMetadataStatus.Failed;
            }
            else
            {
                await MediaRequestMetadataService.ApplyAsync(context, request, metadata, provider);
                await new MediaRequestDuplicateDetector(context).DetectAsync(request);
            }

            await context.SaveChangesAsync();

            logger.LogInformation("Request metadata {Status}: RequestId={RequestId}, Provider={Provider}",
                request.MetadataStatus, requestId, provider);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Request metadata lookup failed: RequestId={RequestId}, Url={Url}", requestId, url);

            await using var failureContext = await contextFactory.CreateDbContextAsync();
            await failureContext.MediaRequests
                                .Where(r => r.Id == requestId && r.ExternalUrl == url)
                                .ExecuteUpdateAsync(s => s.SetProperty(r => r.MetadataStatus, MediaRequestMetadataStatus.Failed));
        }
        finally
        {
            if (delaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        }
    }
}
