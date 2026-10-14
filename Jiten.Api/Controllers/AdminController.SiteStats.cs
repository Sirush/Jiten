using Hangfire;
using Jiten.Api.Jobs;
using Microsoft.AspNetCore.Mvc;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    /// <summary>Rebuilds today's /stats snapshot, then the weekly words-for-coverage block.</summary>
    [HttpPost("site-stats/recompute")]
    public IResult RecomputeSiteStats()
    {
        var snapshot = backgroundJobs.Enqueue<SiteStatsJob>(job => job.Snapshot());
        backgroundJobs.ContinueJobWith<SiteStatsJob>(snapshot, job => job.WordsForCoverage());
        return Results.Accepted(value: new { snapshotJobId = snapshot });
    }
}
