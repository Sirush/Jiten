using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/difficulty-rankings")]
[Produces("application/json")]
[Authorize]
public class DifficultyRankingController(
    JitenDbContext context,
    UserDbContext userContext,
    ICurrentUserService currentUserService) : ControllerBase
{
    private const decimal AutoRankTieThreshold = 0.25m;
    private const int AutoRankMinComparisons = 1;

    private sealed record DeckMeta(DeckSummaryDto Summary, MediaTypeGroup Group);

    [HttpGet]
    public async Task<IResult> GetRankings([FromQuery] MediaTypeGroup? group = null)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();

        var completedDeckIds = await MediaListEntryHelper.CompletedDeckIds(userContext, userId).ToListAsync();

        if (completedDeckIds.Count == 0)
            return Results.Ok(Array.Empty<DifficultyRankingSectionDto>());

        var deckRows = await context.Decks.AsNoTracking()
            .Where(d => completedDeckIds.Contains(d.DeckId) && d.ParentDeckId == null)
            .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.Difficulty, d.MediaType })
            .ToListAsync();

        var deckMap = deckRows.ToDictionary(
            d => d.DeckId,
            d => new DeckMeta(
                MapDeckSummary(d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.CoverName, d.Difficulty, d.MediaType),
                MediaTypeGroups.GetGroup(d.MediaType)));

        var groupFilter = group.HasValue ? new HashSet<MediaTypeGroup> { group.Value } : null;

        var groupsToInit = deckMap.Values
            .Select(d => d.Group)
            .Where(g => groupFilter == null || groupFilter.Contains(g))
            .Distinct()
            .ToList();
        foreach (var g in groupsToInit)
        {
            if (await InitializeFromManualVotes(userId, g, deckMap))
                await SyncDerivedVotes(userId, g);
        }

        var rankGroups = await context.DifficultyRankGroups.AsNoTracking()
            .Where(g => g.UserId == userId && (groupFilter == null || groupFilter.Contains(g.MediaTypeGroup)))
            .Include(g => g.Items)
            .OrderBy(g => g.SortIndex)
            .ToListAsync();

        var sections = new Dictionary<MediaTypeGroup, DifficultyRankingSectionDto>();
        foreach (var deckEntry in deckMap.Values)
        {
            if (groupFilter != null && !groupFilter.Contains(deckEntry.Group)) continue;
            if (!sections.ContainsKey(deckEntry.Group))
                sections[deckEntry.Group] = new DifficultyRankingSectionDto { Group = deckEntry.Group };
        }

        foreach (var groupEntry in sections.Values)
        {
            var groupsForSection = rankGroups
                .Where(g => g.MediaTypeGroup == groupEntry.Group)
                .OrderBy(g => g.SortIndex)
                .ToList();

            var rankedDeckIds = new HashSet<int>();
            foreach (var g in groupsForSection)
            {
                var decks = g.Items
                    .Where(i => deckMap.ContainsKey(i.DeckId))
                    .OrderByDescending(i => i.UpdatedAt)
                    .ThenByDescending(i => i.CreatedAt)
                    .ThenBy(i => deckMap[i.DeckId].Summary.Title)
                    .Select(i => deckMap[i.DeckId].Summary)
                    .ToList();

                foreach (var d in decks) rankedDeckIds.Add(d.Id);

                groupEntry.Groups.Add(new DifficultyRankGroupDto
                {
                    Id = g.Id,
                    SortIndex = g.SortIndex,
                    Decks = decks
                });
            }

            var unranked = deckMap.Values
                .Where(d => d.Group == groupEntry.Group && !rankedDeckIds.Contains(d.Summary.Id))
                .Select(d => d.Summary)
                .OrderBy(d => d.Title)
                .ToList();

            groupEntry.Unranked = unranked;
        }

        return Results.Ok(sections.Values.OrderBy(s => s.Group).ToList());
    }

    [HttpPost("move")]
    public async Task<IResult> Move([FromBody] DifficultyRankingMoveRequest request)
    {
        var userId = currentUserService.UserId;
        if (string.IsNullOrEmpty(userId))
            return Results.Unauthorized();
        var now = DateTimeOffset.UtcNow;

        var deck = await context.Decks
            .Where(d => d.DeckId == request.DeckId)
            .Select(d => new { d.DeckId, d.MediaType, d.ParentDeckId })
            .FirstOrDefaultAsync();
        if (deck == null)
            return Results.NotFound("Deck not found.");
        if (deck.ParentDeckId != null)
            return Results.BadRequest("Difficulty rankings are only available for parent decks, not subdecks.");

        var completed = await MediaListEntryHelper.CompletedDeckIds(userContext, userId, [request.DeckId]).AnyAsync();
        if (!completed)
            return Results.Problem("You must have completed this deck to rank it.", statusCode: 403);

        var group = MediaTypeGroups.GetGroup(deck.MediaType);

        var groups = await context.DifficultyRankGroups
            .Where(g => g.UserId == userId && g.MediaTypeGroup == group)
            .Include(g => g.Items)
            .OrderBy(g => g.SortIndex)
            .ToListAsync();

        DifficultyRankGroup? currentGroup = null;
        DifficultyRankItem? currentItem = null;
        foreach (var g in groups)
        {
            currentItem = g.Items.FirstOrDefault(i => i.DeckId == request.DeckId);
            if (currentItem != null)
            {
                currentGroup = g;
                break;
            }
        }

        int? sourceIndex = null;
        var removedEmptyGroup = false;
        if (currentGroup != null)
            sourceIndex = groups.IndexOf(currentGroup);

        if (currentItem != null)
        {
            currentGroup!.Items.Remove(currentItem);
            context.DifficultyRankItems.Remove(currentItem);
            if (currentGroup.Items.Count == 0)
            {
                context.DifficultyRankGroups.Remove(currentGroup);
                groups.Remove(currentGroup);
                removedEmptyGroup = true;
            }
        }

        switch (request.Mode)
        {
            case DifficultyRankingMoveMode.Unrank:
                break;
            case DifficultyRankingMoveMode.Merge:
            {
                if (request.TargetGroupId == null)
                    return Results.BadRequest("TargetGroupId is required for merge.");

                var target = groups.FirstOrDefault(g => g.Id == request.TargetGroupId.Value);
                if (target == null)
                    return Results.BadRequest("Target group not found.");

                var newItem = new DifficultyRankItem
                {
                    UserId = userId,
                    DeckId = request.DeckId,
                    Group = target,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                target.Items.Add(newItem);
                context.DifficultyRankItems.Add(newItem);
                break;
            }
            case DifficultyRankingMoveMode.Insert:
            {
                if (request.InsertIndex == null)
                    return Results.BadRequest("InsertIndex is required for insert.");

                var insertIndex = Math.Clamp(request.InsertIndex.Value, 0, groups.Count);
                if (removedEmptyGroup && sourceIndex.HasValue && sourceIndex.Value < insertIndex)
                    insertIndex = Math.Max(0, insertIndex - 1);
                var newGroup = new DifficultyRankGroup
                {
                    UserId = userId,
                    MediaTypeGroup = group,
                    SortIndex = insertIndex,
                    CreatedAt = now
                };
                var newItem = new DifficultyRankItem
                {
                    UserId = userId,
                    DeckId = request.DeckId,
                    Group = newGroup,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                newGroup.Items.Add(newItem);
                groups.Insert(insertIndex, newGroup);
                context.DifficultyRankGroups.Add(newGroup);
                context.DifficultyRankItems.Add(newItem);
                break;
            }
            default:
                return Results.BadRequest("Invalid move mode.");
        }

        var needsReindex = removedEmptyGroup || request.Mode == DifficultyRankingMoveMode.Insert;
        if (needsReindex)
            await SaveGroupsWithReindex(groups);
        else
            await context.SaveChangesAsync();
        await SyncDerivedVotes(userId, group);

        var result = await GetRankings(group);
        return result;
    }

    private async Task<bool> InitializeFromManualVotes(string userId, MediaTypeGroup group, Dictionary<int, DeckMeta> deckMap)
    {
        var hasGroups = await context.DifficultyRankGroups
            .AsNoTracking()
            .AnyAsync(g => g.UserId == userId && g.MediaTypeGroup == group);
        if (hasGroups)
            return false;

        var groupDeckIds = deckMap
            .Where(kvp => kvp.Value.Group == group)
            .Select(kvp => kvp.Key)
            .ToHashSet();
        if (groupDeckIds.Count < 2)
            return false;

        var votes = await context.DifficultyVotes.AsNoTracking()
            .Where(v => v.UserId == userId
                && v.IsValid
                && v.Source == DifficultyVoteSource.Manual
                && groupDeckIds.Contains(v.DeckLowId)
                && groupDeckIds.Contains(v.DeckHighId))
            .Select(v => new { v.DeckLowId, v.DeckHighId, v.Outcome })
            .ToListAsync();

        if (votes.Count == 0)
            return false;

        var sumByDeck = new Dictionary<int, int>();
        var countByDeck = new Dictionary<int, int>();

        void AddDelta(int deckId, int delta)
        {
            sumByDeck[deckId] = sumByDeck.GetValueOrDefault(deckId) + delta;
            countByDeck[deckId] = countByDeck.GetValueOrDefault(deckId) + 1;
        }

        foreach (var v in votes)
        {
            var outcome = (int)v.Outcome;
            AddDelta(v.DeckLowId, outcome);
            AddDelta(v.DeckHighId, -outcome);
        }

        var scoredDecks = sumByDeck
            .Select(kvp => new
            {
                DeckId = kvp.Key,
                Score = kvp.Value / (decimal)countByDeck[kvp.Key],
                Count = countByDeck[kvp.Key]
            })
            .Where(d => d.Count >= AutoRankMinComparisons)
            .OrderBy(d => d.Score)
            .ThenBy(d => deckMap[d.DeckId].Summary.Title)
            .ToList();

        if (scoredDecks.Count < 2)
            return false;

        var now = DateTimeOffset.UtcNow;
        var groups = new List<DifficultyRankGroup>();
        DifficultyRankGroup? current = null;
        decimal? lastScore = null;

        foreach (var entry in scoredDecks)
        {
            if (current == null || (lastScore.HasValue && Math.Abs(entry.Score - lastScore.Value) > AutoRankTieThreshold))
            {
                current = new DifficultyRankGroup
                {
                    UserId = userId,
                    MediaTypeGroup = group,
                    SortIndex = groups.Count,
                    CreatedAt = now
                };
                groups.Add(current);
            }

            var item = new DifficultyRankItem
            {
                UserId = userId,
                DeckId = entry.DeckId,
                Group = current,
                CreatedAt = now,
                UpdatedAt = now
            };
            current.Items.Add(item);
            lastScore = entry.Score;
        }

        context.DifficultyRankGroups.AddRange(groups);
        try
        {
            await context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            return false;
        }
        return true;
    }

    private Task SyncDerivedVotes(string userId, MediaTypeGroup group)
        => DifficultyRankingSync.SyncDerivedVotes(context, userContext, userId, group);

    private async Task SaveGroupsWithReindex(List<DifficultyRankGroup> groups)
    {
        if (groups.Count == 0)
        {
            await context.SaveChangesAsync();
            return;
        }

        var minIndex = groups.Min(g => g.SortIndex);
        for (var i = 0; i < groups.Count; i++)
            groups[i].SortIndex = minIndex - 1 - i;

        await context.SaveChangesAsync();

        for (var i = 0; i < groups.Count; i++)
            groups[i].SortIndex = i;

        await context.SaveChangesAsync();
    }

    private static DeckSummaryDto MapDeckSummary(int deckId, string title, string? romajiTitle, string? englishTitle, string coverName, float difficulty, MediaType mediaType) => new()
    {
        Id = deckId,
        Title = title,
        RomajiTitle = romajiTitle,
        EnglishTitle = englishTitle,
        CoverUrl = coverName,
        Difficulty = difficulty,
        MediaType = mediaType
    };
}
