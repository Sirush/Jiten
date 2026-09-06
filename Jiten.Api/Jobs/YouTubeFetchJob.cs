using Hangfire;
using Jiten.Api.Services;
using Jiten.Core.YouTube;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Jobs;

public class YouTubeFetchJob(
    YouTubeDrainService drainService,
    IBackgroundJobClient backgroundJobs,
    IOptions<YouTubeOptions> options,
    ILogger<YouTubeFetchJob> logger)
{
    /// <summary>
    /// Server-side drain of one source's Pending rows. Stops at the first bot-check so a flagged egress does
    /// not burn the whole queue; the rows stay Pending for the home CLI.
    /// </summary>
    [Queue(YouTubeQueues.Fetch)]
    [AutomaticRetry(Attempts = 0)]
    public async Task Drain(int sourceDeckId)
    {
        var result = await drainService.DrainAsync(sourceDeckId, options.Value.FetchBatchSize);

        logger.LogInformation("YouTubeFetch: source {DeckId} checked {Checked}, fetched {Fetched}, skipped {Skipped}{Blocked}",
                              sourceDeckId, result.Checked, result.Fetched, result.Skipped,
                              result.Blocked ? " (stopped: bot check)" : "");

        if (result.Fetched > 0)
            backgroundJobs.Enqueue<YouTubeImportJob>(job => job.ImportFetched(sourceDeckId));

        // A full batch means more may be waiting; keep going on the same single-worker queue
        if (!result.Blocked && result.Checked >= options.Value.FetchBatchSize)
            backgroundJobs.Enqueue<YouTubeFetchJob>(job => job.Drain(sourceDeckId));
    }
}
