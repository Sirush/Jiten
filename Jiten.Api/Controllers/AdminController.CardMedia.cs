using Hangfire;
using Jiten.Api.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    private const int MaxCardMediaPreviewRows = 200;

    /// <summary>
    /// Lists the card-media images stored without normalization
    /// </summary>
    [HttpGet("card-media/renormalize/preview")]
    public async Task<IResult> PreviewCardMediaRenormalize([FromQuery] int take = 50)
    {
        var limit = Math.Clamp(take, 1, MaxCardMediaPreviewRows);

        var candidates = CardMediaRenormalizeJob.Candidates(userContext).AsNoTracking();

        var totalCount = await candidates.CountAsync();
        var totalBytes = totalCount == 0 ? 0 : await candidates.SumAsync(m => m.FileSizeBytes);

        var byContentType = await candidates
                                  .GroupBy(m => m.ContentType)
                                  .Select(g => new
                                               {
                                                   contentType = g.Key,
                                                   count = g.Count(),
                                                   bytes = g.Sum(m => m.FileSizeBytes)
                                               })
                                  .OrderByDescending(g => g.bytes)
                                  .ToListAsync();

        var rows = await candidates
                         .OrderByDescending(m => m.FileSizeBytes)
                         .Take(limit)
                         .Select(m => new
                                      {
                                          m.Id,
                                          m.WordId,
                                          m.ReadingIndex,
                                          m.ContentType,
                                          m.FileSizeBytes,
                                          m.CreatedAt
                                      })
                         .ToListAsync();

        var labels = await WordLabelsAsync(rows.Select(r => r.WordId).Distinct().ToList());

        var items = rows.Select(r => new
                                     {
                                         mediaId = r.Id,
                                         wordId = r.WordId,
                                         readingIndex = r.ReadingIndex,
                                         word = labels.GetValueOrDefault((r.WordId, (short)r.ReadingIndex))
                                                ?? labels.GetValueOrDefault((r.WordId, (short)0)),
                                         contentType = r.ContentType,
                                         fileSizeBytes = r.FileSizeBytes,
                                         createdAt = r.CreatedAt
                                     });

        return Results.Ok(new
                          {
                              totalCount,
                              totalBytes,
                              byContentType,
                              items,
                              truncated = totalCount > limit,
                              retained = await RetainedOriginalsAsync()
                          });
    }

    /// <param name="dryRun">
    /// Runs the identical download-and-encode pass and writes nothing, so the logged saving is measured
    /// rather than estimated. Defaults to true: the live pass has to be asked for.
    /// </param>
    [HttpPost("card-media/renormalize")]
    public IResult QueueCardMediaRenormalize([FromQuery] bool dryRun = true)
    {
        backgroundJobs.Enqueue<CardMediaRenormalizeJob>(job => job.RenormalizeAll(dryRun));
        logger.LogInformation("Admin queued card-media renormalize (dryRun: {DryRun})", dryRun);
        return Results.Ok(new { queued = true, dryRun });
    }

    [HttpGet("card-media/renormalize/status")]
    public async Task<IResult> CardMediaRenormalizeStatus()
    {
        var pending = await CardMediaRenormalizeJob.Candidates(userContext).AsNoTracking().CountAsync();
        return Results.Ok(new { pending, retained = await RetainedOriginalsAsync() });
    }

    /// <summary>
    /// Points every rewritten row back at its original file and deletes the file the backfill wrote.
    /// Safe to run at any time: the originals were never deleted.
    /// </summary>
    [HttpPost("card-media/renormalize/rollback")]
    public async Task<IResult> RollbackCardMediaRenormalize()
    {
        var eligible = await CardMediaRenormalizeJob.Retained(userContext).AsNoTracking().CountAsync();

        backgroundJobs.Enqueue<CardMediaRenormalizeJob>(job => job.RollbackAll());
        logger.LogInformation("Admin queued card-media renormalize rollback for {Count} rows", eligible);

        return Results.Ok(new { queued = true, eligible });
    }

    /// <summary>
    /// Deletes the superseded originals. This is what makes the backfill irreversible, so it is opt-in.
    /// </summary>
    [HttpPost("card-media/renormalize/discard-originals")]
    public async Task<IResult> DiscardCardMediaOriginals([FromQuery] bool confirm = false)
    {
        if (!confirm)
            return Results.BadRequest(new { error = "Pass confirm=true: deleting the originals cannot be undone." });

        var eligible = await CardMediaRenormalizeJob.Retained(userContext).AsNoTracking().CountAsync();

        backgroundJobs.Enqueue<CardMediaRenormalizeJob>(job => job.DiscardOriginals());
        logger.LogInformation("Admin queued card-media original discard for {Count} files", eligible);

        return Results.Ok(new { queued = true, eligible });
    }

    /// <summary>Rows still holding a superseded original, which are the ones a rollback would restore.</summary>
    private async Task<object?> RetainedOriginalsAsync() =>
        await CardMediaRenormalizeJob.Retained(userContext).AsNoTracking()
                                     .GroupBy(_ => 1)
                                     .Select(g => new
                                                  {
                                                      count = g.Count(),
                                                      oldBytes = g.Sum(m => m.PreviousFileSizeBytes!.Value),
                                                      newBytes = g.Sum(m => m.FileSizeBytes)
                                                  })
                                     .FirstOrDefaultAsync();

    /// <summary>Form text per (word, reading index), so a row is identified by the card it belongs to.</summary>
    private async Task<Dictionary<(int WordId, short ReadingIndex), string>> WordLabelsAsync(List<int> wordIds)
    {
        if (wordIds.Count == 0)
            return [];

        var forms = await dbContext.WordForms.AsNoTracking()
                                   .Where(f => wordIds.Contains(f.WordId))
                                   .Select(f => new { f.WordId, f.ReadingIndex, f.Text })
                                   .ToListAsync();

        return forms.GroupBy(f => (f.WordId, f.ReadingIndex))
                    .ToDictionary(g => g.Key, g => g.First().Text);
    }
}
