using Jiten.Core;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public class ParserWarmupService(IDbContextFactory<JitenDbContext> contextFactory, StartupReadiness readiness,
                                 ILogger<ParserWarmupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            logger.LogInformation("Parser warmup starting");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Parser.Parser.WarmupAsync(contextFactory, msg => logger.LogInformation("{Msg}", msg));
            sw.Stop();
            logger.LogInformation("Parser warmup completed in {ElapsedMs}ms", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Parser warmup failed");
        }
        finally
        {
            readiness.MarkReady(StartupReadiness.Parser);
        }
    }
}
