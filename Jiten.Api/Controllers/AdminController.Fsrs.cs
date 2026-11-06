using Hangfire;
using Jiten.Api.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    private const int MaxInlineAutoOptimiseUsers = 50;
    private const int MaxQueuedAutoOptimiseUsers = 1000;

    /// <summary>Runs the nightly FSRS refit now; inline runs are capped to keep the request short, background dry runs log their decisions.</summary>
    [HttpPost("fsrs/auto-optimise/run")]
    public async Task<IResult> RunFsrsAutoOptimise([FromServices] FsrsAutoOptimiseJob job, [FromQuery] bool dryRun = true, [FromQuery] int limit = 20,
                                                   [FromQuery] string? userId = null, [FromQuery] bool background = false)
    {
        if (background)
        {
            if (!dryRun)
                return Results.BadRequest(new { error = "Background runs are dry runs only; the nightly schedule does the real run." });

            var queuedLimit = Math.Clamp(limit, 1, MaxQueuedAutoOptimiseUsers);
            var jobId = backgroundJobs.Enqueue<FsrsAutoOptimiseJob>(j => j.DryRun(queuedLimit));
            logger.LogInformation("Admin queued FSRS auto-optimise dry run for {Limit} users", queuedLimit);
            return Results.Ok(new { queued = true, jobId, limit = queuedLimit });
        }

        var decisions = await job.Sweep(dryRun, Math.Clamp(limit, 1, MaxInlineAutoOptimiseUsers), userId);
        logger.LogInformation("Admin ran FSRS auto-optimise inline: {Count} users, dry run {DryRun}", decisions.Count, dryRun);
        return Results.Ok(decisions);
    }
}
