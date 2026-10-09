using Jiten.Core;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public sealed class FranchiseSyncRunner(
    IDbContextFactory<JitenDbContext> contextFactory,
    IDbContextFactory<UserDbContext> userContextFactory,
    FranchiseResponseCache responseCache,
    IStudyDeckMembershipService membershipService,
    ILogger<FranchiseSyncRunner> logger)
{
    public async Task<FranchiseSyncSummary> RunAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var summary = await FranchiseSync.RunAsync(db, userContextFactory.CreateDbContext, ct);
        responseCache.Invalidate();
        await InvalidateMediaGroupDeckSetsAsync(ct);

        logger.LogInformation("Franchise sync: {Created} created, {Renamed} renamed, {Merged} merged, {Deleted} deleted, {Unchanged} unchanged, {Decks} decks moved",
                              summary.Created, summary.Renamed, summary.Merged, summary.Deleted, summary.Unchanged, summary.DecksUpdated);
        return summary;
    }

    // Group study decks cache their word set per deck; links and series edits change which decks a group draws from.
    private async Task InvalidateMediaGroupDeckSetsAsync(CancellationToken ct)
    {
        await using var userDb = await userContextFactory.CreateDbContextAsync(ct);
        var groupDecks = await userDb.UserStudyDecks.AsNoTracking()
                                     .Where(sd => sd.DeckType == StudyDeckType.MediaGroup)
                                     .Select(sd => new { sd.UserId, sd.UserStudyDeckId })
                                     .ToListAsync(ct);
        foreach (var sd in groupDecks)
            await membershipService.Invalidate(sd.UserId, sd.UserStudyDeckId);
    }
}
