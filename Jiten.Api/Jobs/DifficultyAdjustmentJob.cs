using Hangfire;
using Jiten.Core;
using Jiten.Core.Difficulty;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

public class DifficultyAdjustmentJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IDbContextFactory<UserDbContext> userContextFactory,
    ILogger<DifficultyAdjustmentJob> logger)
{
    [Queue("stats")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 300, 900])]
    public async Task ComputeAllAdjustments()
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        await using var userContext = await userContextFactory.CreateDbContextAsync();

        var (decks, votes, ratings, users) = await LoadInputsAsync(context, userContext);
        var results = DifficultyAdjustmentCalculator.Compute(decks, votes, ratings, users, DateTime.UtcNow);

        var deckIds = results.Select(r => r.DeckId).ToHashSet();
        var deckDifficulties = await context.DeckDifficulties
                                            .Where(dd => deckIds.Contains(dd.DeckId))
                                            .ToDictionaryAsync(dd => dd.DeckId);

        foreach (var r in results)
        {
            if (!deckDifficulties.TryGetValue(r.DeckId, out var dd)) continue;
            dd.EasierVoteCount = r.EasierVoteCount;
            dd.HarderVoteCount = r.HarderVoteCount;
            dd.DistinctVoterCount = r.DistinctVoterCount;
            dd.UserAdjustment = r.Adjustment;
            dd.NEffective = r.Neff;
            dd.AdjustmentConfidence = r.Confidence;
        }

        await context.SaveChangesAsync();
        logger.LogInformation("Difficulty adjustment completed for {Count} decks", results.Count);
    }

    /// <summary>
    /// Loads the inputs the calculator needs. Shared with the read-only backtest so both see identical data.
    /// </summary>
    public static async Task<(
        List<DeckDifficultyInput> decks,
        List<DifficultyVoteInput> votes,
        List<DifficultyRatingInput> ratings,
        List<UserDifficultyInput> users)> LoadInputsAsync(JitenDbContext context, UserDbContext userContext)
    {
        var votes = await context.DifficultyVotes
                                 .Where(v => v.IsValid)
                                 .Select(v => new DifficultyVoteInput(
                                     v.Id, v.UserId, v.DeckLowId, v.DeckHighId, v.Outcome, v.Source))
                                 .ToListAsync();

        var ratings = await context.DifficultyRatings
                                   .Select(r => new DifficultyRatingInput(r.UserId, r.DeckId, r.Rating))
                                   .ToListAsync();

        var referencedDeckIds = votes
                                .SelectMany(v => new[] { v.DeckLowId, v.DeckHighId })
                                .Union(ratings.Select(r => r.DeckId))
                                .ToHashSet();

        var deckTypes = await context.Decks
                                     .Where(d => referencedDeckIds.Contains(d.DeckId))
                                     .Select(d => new { d.DeckId, d.MediaType })
                                     .ToListAsync();
        var deckTypeMap = deckTypes.ToDictionary(d => d.DeckId, d => d.MediaType);

        var decks = await context.DeckDifficulties
                                 .Where(dd => referencedDeckIds.Contains(dd.DeckId))
                                 .Select(dd => new { dd.DeckId, dd.Difficulty })
                                 .ToListAsync();
        var deckInputs = decks
                         .Where(dd => deckTypeMap.ContainsKey(dd.DeckId))
                         .Select(dd => new DeckDifficultyInput(dd.DeckId, deckTypeMap[dd.DeckId], dd.Difficulty))
                         .ToList();

        var relevantUserIds = votes.Select(v => v.UserId)
                                   .Union(ratings.Select(r => r.UserId))
                                   .ToHashSet();
        var users = await userContext.Users
                                     .Where(u => relevantUserIds.Contains(u.Id))
                                     .Select(u => new UserDifficultyInput(u.Id, u.CreatedAt))
                                     .ToListAsync();

        return (deckInputs, votes, ratings, users);
    }
}
