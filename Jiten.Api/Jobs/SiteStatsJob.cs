using System.Text.Json;
using System.Text.Json.Nodes;
using Hangfire;
using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Jobs;

public class SiteStatsJob(
    SiteStatsBuilder builder,
    IDbContextFactory<JitenDbContext> jitenFactory,
    IMemoryCache cache,
    ILogger<SiteStatsJob> logger)
{
    public static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public const int HistoryDays = 400;
    public const int MinTitlesPerCoverageType = 50;
    public static readonly int[] CoverageLevels = [80, 85, 90, 95, 98, 99];

    private static readonly string WordsForCoverageKey = JsonNamingPolicy.CamelCase.ConvertName(nameof(SiteStatsDto.WordsForCoverage));

    [Queue("stats")]
    [DisableConcurrentExecution(60 * 60)]
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [300, 1800])]
    public async Task Snapshot()
    {
        var dto = await builder.BuildAsync(DateTime.UtcNow);

        await using var context = await jitenFactory.CreateDbContextAsync();

        // Words-for-coverage has its own, slower schedule; carry the latest result forward.
        var latest = await context.SiteStatsSnapshots.AsNoTracking().OrderByDescending(s => s.Date).FirstOrDefaultAsync();
        if (latest != null && JsonNode.Parse(latest.Payload)?[WordsForCoverageKey] is { } carried)
            dto.WordsForCoverage = carried.Deserialize<SiteStatsWordsForCoverageDto>(PayloadJson);

        await Upsert(context, dto.Date, dto.ComputedAt, JsonSerializer.Serialize(dto, PayloadJson));

        var cutoff = dto.Date.AddDays(-HistoryDays);
        await context.SiteStatsSnapshots.Where(s => s.Date < cutoff).ExecuteDeleteAsync();
    }

    /// <summary>Only changes when frequency ranks or the library change, and reads every title's words, so it runs weekly rather than nightly.</summary>
    [Queue("stats")]
    [DisableConcurrentExecution(60 * 60)]
    [AutomaticRetry(Attempts = 1)]
    public async Task WordsForCoverage()
    {
        await using var context = await jitenFactory.CreateDbContextAsync();
        if (context.Database.ProviderName?.Contains("Npgsql") != true)
        {
            logger.LogInformation("Words-for-coverage needs Postgres binary COPY; skipped on {Provider}", context.Database.ProviderName);
            return;
        }

        var results = await Core.Services.WordsForCoverage.ComputeAsync(jitenFactory, new Core.Services.WordsForCoverage.Options(),
                                                                       message => logger.LogInformation("{Message}", message));
        var block = ToDto(results, DateTime.UtcNow);

        var latest = await context.SiteStatsSnapshots.AsNoTracking().OrderByDescending(s => s.Date).FirstOrDefaultAsync();
        if (latest == null)
        {
            await Snapshot();
            latest = await context.SiteStatsSnapshots.AsNoTracking().OrderByDescending(s => s.Date).FirstAsync();
        }

        var payload = JsonNode.Parse(latest.Payload)!.AsObject();
        payload[WordsForCoverageKey] = JsonSerializer.SerializeToNode(block, PayloadJson);
        await Upsert(context, latest.Date, latest.ComputedAt, payload.ToJsonString(PayloadJson));
    }

    public static SiteStatsWordsForCoverageDto ToDto(IEnumerable<WordsForCoverageResult> results, DateTime computedAt)
    {
        var levelIndexes = CoverageLevels.Select(l => Array.FindIndex(Core.Services.WordsForCoverage.Levels, x => Math.Abs(x * 100 - l) < 0.001))
                                         .ToArray();
        return new SiteStatsWordsForCoverageDto
        {
            ComputedAt = computedAt,
            Levels = CoverageLevels.ToList(),
            MediaTypes = results.Where(r => r.Titles >= MinTitlesPerCoverageType)
                                .OrderBy(r => r.MedianRanks[levelIndexes[0]])
                                .Select(r => new SiteStatsWordsForCoverageTypeDto
                                {
                                    MediaType = r.MediaType,
                                    Titles = r.Titles,
                                    Ranks = levelIndexes.Select(i => r.MedianRanks[i] >= int.MaxValue / 2.0 ? (int?)null : (int)Math.Round(r.MedianRanks[i]))
                                                        .ToList(),
                                    ExcludedPercent = Math.Round(r.MedianExcludedPercent, 1)
                                })
                                .ToList()
        };
    }

    private static async Task UpsertRow(JitenDbContext context, DateOnly date, DateTime computedAt, string payload)
    {
        var row = await context.SiteStatsSnapshots.FirstOrDefaultAsync(s => s.Date == date);
        if (row == null)
            context.SiteStatsSnapshots.Add(new SiteStatsSnapshot { Date = date, ComputedAt = computedAt, Payload = payload });
        else
        {
            row.ComputedAt = computedAt;
            row.Payload = payload;
        }

        await context.SaveChangesAsync();
    }

    private async Task Upsert(JitenDbContext context, DateOnly date, DateTime computedAt, string payload)
    {
        await UpsertRow(context, date, computedAt, payload);
        cache.Remove(Controllers.StatsController.SiteStatsCacheKey);
    }
}
