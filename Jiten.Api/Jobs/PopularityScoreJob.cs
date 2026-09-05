using Hangfire;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Services.Popularity;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

public class PopularityScoreJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IDbContextFactory<UserDbContext> userContextFactory,
    ILogger<PopularityScoreJob> logger)
{
    private static readonly TimeSpan ActivityWindow = TimeSpan.FromDays(PopularityWeights.TrendingBaselineDays);

    private sealed record Counts(int InLists, int Favourites, int StudyDecks);

    [Queue("stats")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 300, 900])]
    public async Task RecomputeAll()
    {
        var now = DateTime.UtcNow;
        await using var context = await contextFactory.CreateDbContextAsync();
        await using var userContext = await userContextFactory.CreateDbContextAsync();

        var decks = await context.Decks.AsNoTracking()
                                 .Select(d => new { d.DeckId, d.ParentDeckId, d.MediaType, d.CreationDate, d.ExternalRating, d.ReleaseDate })
                                 .ToListAsync();
        var nodes = decks.Select(d => new DeckNode(d.DeckId, d.ParentDeckId, d.MediaType, d.CreationDate.UtcDateTime, d.ExternalRating, d.ReleaseDate)).ToList();
        var rootOf = nodes.ToDictionary(n => n.DeckId, n => n.ParentDeckId ?? n.DeckId);

        var intents = new List<IntentEvent>();
        var listUsers = new Dictionary<int, HashSet<string>>();
        var favouriteUsers = new Dictionary<int, HashSet<string>>();
        var studyUsers = new Dictionary<int, HashSet<string>>();

        static void Track(Dictionary<int, HashSet<string>> map, int root, string userId)
        {
            if (!map.TryGetValue(root, out var set)) map[root] = set = new HashSet<string>();
            set.Add(userId);
        }

        var preferences = await userContext.UserDeckPreferences.AsNoTracking()
                                           .Select(p => new { p.UserId, p.DeckId, p.Status, p.IsFavourite, p.IsIgnored, p.UpdatedAt })
                                           .ToListAsync();
        foreach (var p in preferences)
        {
            var statusWeight = PopularityWeights.ForStatus(p.Status);
            if (statusWeight != 0) intents.Add(new IntentEvent(p.DeckId, statusWeight, p.UpdatedAt, p.UserId));
            if (p.IsFavourite) intents.Add(new IntentEvent(p.DeckId, PopularityWeights.Favourite, p.UpdatedAt, p.UserId));
            if (p.IsIgnored) intents.Add(new IntentEvent(p.DeckId, PopularityWeights.Ignored, p.UpdatedAt, p.UserId));
            if (!rootOf.TryGetValue(p.DeckId, out var root)) continue;
            if (p.Status != DeckStatus.None) Track(listUsers, root, p.UserId);
            if (p.IsFavourite) Track(favouriteUsers, root, p.UserId);
        }

        var studyDecks = await userContext.UserStudyDecks.AsNoTracking()
                                          .Where(s => s.DeckId != null)
                                          .Select(s => new { s.UserId, s.DeckId, s.CreatedAt })
                                          .ToListAsync();
        intents.AddRange(studyDecks.GroupBy(s => new { s.UserId, s.DeckId })
                                   .Select(g => new IntentEvent(g.Key.DeckId!.Value, PopularityWeights.StudyDeck, g.Min(s => s.CreatedAt), g.Key.UserId)));
        foreach (var s in studyDecks)
        {
            if (rootOf.TryGetValue(s.DeckId!.Value, out var root)) Track(studyUsers, root, s.UserId);
        }

        var downloads = await userContext.DeckDownloads.AsNoTracking()
                                         .Select(d => new { d.UserId, d.DeckId, d.FirstDownloadAt })
                                         .ToListAsync();
        intents.AddRange(downloads.Select(d => new IntentEvent(d.DeckId, PopularityWeights.Download, d.FirstDownloadAt, d.UserId)));

        var requestDecks = await context.MediaRequests.AsNoTracking()
                                        .Where(r => r.FulfilledDeckId != null || r.TargetDeckId != null)
                                        .Select(r => new { r.Id, r.FulfilledDeckId, r.TargetDeckId })
                                        .ToListAsync();
        var deckByRequest = requestDecks.ToDictionary(r => r.Id, r => r.FulfilledDeckId ?? r.TargetDeckId!.Value);
        var requestIds = deckByRequest.Keys.ToList();

        var upvotes = await context.MediaRequestUpvotes.AsNoTracking()
                                   .Where(u => requestIds.Contains(u.MediaRequestId))
                                   .Select(u => new { u.MediaRequestId, u.UserId, u.CreatedAt })
                                   .ToListAsync();
        intents.AddRange(upvotes.GroupBy(u => new { u.UserId, DeckId = deckByRequest[u.MediaRequestId] })
                                .Select(g => new IntentEvent(g.Key.DeckId, PopularityWeights.Upvote, g.Min(u => u.CreatedAt), g.Key.UserId)));

        var boosts = await context.MediaRequestBoosts.AsNoTracking()
                                  .Where(b => requestIds.Contains(b.MediaRequestId))
                                  .Select(b => new { b.MediaRequestId, b.UserId, b.CreatedAt })
                                  .ToListAsync();
        intents.AddRange(boosts.GroupBy(b => new { b.UserId, DeckId = deckByRequest[b.MediaRequestId] })
                               .Select(g => new IntentEvent(g.Key.DeckId, PopularityWeights.Boost, g.Min(b => b.CreatedAt), g.Key.UserId)));

        var since = DateOnly.FromDateTime(now - ActivityWindow);
        var activityRows = await context.DeckActivityDailies.AsNoTracking()
                                        .Where(a => a.Date >= since)
                                        .Select(a => new { a.DeckId, a.Date, a.Views, a.GuestDownloads })
                                        .ToListAsync();
        var activity = activityRows.Select(a => new ActivityDay(a.DeckId, a.Date, a.Views, a.GuestDownloads)).ToList();

        var results = PopularityCalculator.Compute(nodes, intents, activity, now);
        var counts = results.Keys.ToDictionary(id => id, id => new Counts(
            listUsers.GetValueOrDefault(id)?.Count ?? 0,
            favouriteUsers.GetValueOrDefault(id)?.Count ?? 0,
            studyUsers.GetValueOrDefault(id)?.Count ?? 0));
        await WriteAsync(context, results, counts);

        logger.LogInformation("Popularity recomputed for {Decks} parent decks from {Intents} intent events and {Activity} activity days; {Trending} trending",
                              results.Count, intents.Count, activity.Count, results.Values.Count(r => r.IsTrending));
    }

    private static async Task WriteAsync(JitenDbContext context, Dictionary<int, PopularityResult> results, Dictionary<int, Counts> counts)
    {
        if (context.Database.ProviderName?.Contains("Npgsql") == true)
        {
            var ids = results.Keys.ToArray();
            var scores = ids.Select(id => results[id].Score).ToArray();
            var typeRanks = ids.Select(id => results[id].TypeRank).ToArray();
            var globalRanks = ids.Select(id => results[id].GlobalRank).ToArray();
            var trending = ids.Select(id => results[id].IsTrending).ToArray();
            var inLists = ids.Select(id => counts[id].InLists).ToArray();
            var favourites = ids.Select(id => counts[id].Favourites).ToArray();
            var studyDecks = ids.Select(id => counts[id].StudyDecks).ToArray();
            await context.Database.ExecuteSqlRawAsync(
                "UPDATE jiten.\"Decks\" AS d SET \"PopularityScore\" = v.score, \"PopularityRank\" = v.type_rank, " +
                "\"PopularityGlobalRank\" = v.global_rank, \"IsTrending\" = v.trending, \"PopularityListCount\" = v.in_lists, " +
                "\"PopularityFavouriteCount\" = v.favourites, \"PopularityStudyDeckCount\" = v.study_decks " +
                "FROM unnest({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}) AS v(id, score, type_rank, global_rank, trending, in_lists, favourites, study_decks) " +
                "WHERE d.\"DeckId\" = v.id AND (d.\"PopularityScore\" <> v.score OR d.\"PopularityRank\" <> v.type_rank " +
                "OR d.\"PopularityGlobalRank\" <> v.global_rank OR d.\"IsTrending\" <> v.trending OR d.\"PopularityListCount\" <> v.in_lists " +
                "OR d.\"PopularityFavouriteCount\" <> v.favourites OR d.\"PopularityStudyDeckCount\" <> v.study_decks)",
                ids, scores, typeRanks, globalRanks, trending, inLists, favourites, studyDecks);
            return;
        }

        var parents = await context.Decks.Where(d => d.ParentDeckId == null).ToListAsync();
        foreach (var deck in parents)
        {
            var r = results.GetValueOrDefault(deck.DeckId);
            var c = counts.GetValueOrDefault(deck.DeckId) ?? new Counts(0, 0, 0);
            deck.PopularityScore = r.Score;
            deck.PopularityRank = r.TypeRank;
            deck.PopularityGlobalRank = r.GlobalRank;
            deck.IsTrending = r.IsTrending;
            deck.PopularityListCount = c.InLists;
            deck.PopularityFavouriteCount = c.Favourites;
            deck.PopularityStudyDeckCount = c.StudyDecks;
        }
        await context.SaveChangesAsync();
    }
}
