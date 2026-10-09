using Jiten.Api.Dtos;
using Jiten.Api.Dtos.Requests;
using Jiten.Api.Services;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

public partial class AdminController
{
    private const int MaxCycleSearchNodes = 5000;

    [HttpGet("series")]
    public async Task<PaginatedResponse<List<SeriesSummaryDto>>> ListSeries([FromServices] SeriesService series, string? query = null,
                                                                             SeriesKind? kind = null, int offset = 0, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(offset, 0);

        var filtered = SeriesService.Filter(dbContext.Series.AsNoTracking(), query, kind);
        var total = await filtered.CountAsync();
        var page = await filtered.OrderBy(s => s.Name).ThenBy(s => s.SeriesId).Skip(offset).Take(limit).ToListAsync();

        return new PaginatedResponse<List<SeriesSummaryDto>>(await series.BuildSummariesAsync(page), total, limit, offset);
    }

    [HttpGet("series/{id:int}")]
    public async Task<IActionResult> GetSeriesAdmin(int id, [FromServices] SeriesService series)
    {
        var detail = await series.BuildDetailAsync(id);
        return detail == null ? NotFound() : Ok(detail);
    }

    [HttpPost("series")]
    public async Task<IActionResult> CreateSeries([FromBody] CreateSeriesRequest request, [FromServices] SeriesService series)
    {
        var error = SeriesService.ValidateName(request.Name);
        if (error == null && !Enum.IsDefined(request.Kind))
            error = $"Unknown series kind {(int)request.Kind}.";
        if (error != null)
            return BadRequest(new { Message = error });

        var entity = new Series { Name = request.Name.Trim(), Kind = request.Kind };
        dbContext.Series.Add(entity);
        await dbContext.SaveChangesAsync();

        logger.LogInformation("Admin created series {SeriesId} ({Kind}) {Name}", entity.SeriesId, entity.Kind, entity.Name);
        return Ok(await series.ToRefAsync(entity.SeriesId));
    }

    [HttpPatch("series/{id:int}")]
    public async Task<IActionResult> UpdateSeries(int id, [FromBody] UpdateSeriesRequest request, [FromServices] SeriesService series,
                                                  [FromServices] FranchiseSyncRunner franchiseSync)
    {
        var entity = await dbContext.Series.FirstOrDefaultAsync(s => s.SeriesId == id);
        if (entity == null)
            return NotFound();

        if (request.Name != null)
        {
            var nameError = SeriesService.ValidateName(request.Name);
            if (nameError != null)
                return BadRequest(new { Message = nameError });
            entity.Name = request.Name.Trim();
        }

        entity.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
        await franchiseSync.RunAsync();

        logger.LogInformation("Admin updated series {SeriesId}", id);
        return Ok(await series.ToRefAsync(entity.SeriesId));
    }

    [HttpDelete("series/{id:int}")]
    public async Task<IActionResult> DeleteSeries(int id, [FromServices] FranchiseSyncRunner franchiseSync)
    {
        var entity = await dbContext.Series.FirstOrDefaultAsync(s => s.SeriesId == id);
        if (entity == null)
            return NotFound();

        var successor = await SeriesHome.FranchiseAsync(dbContext, id);

        await dbContext.SeriesMembers.Where(m => m.SeriesId == id).ExecuteDeleteAsync();
        dbContext.Series.Remove(entity);
        await dbContext.SaveChangesAsync();

        var repointed = successor is { } franchiseId
            ? await FranchiseSync.RepointStudyDecksAsync(userContext, MediaGroupKind.Series, id, franchiseId, newKind: MediaGroupKind.Franchise)
            : 0;
        await franchiseSync.RunAsync();

        logger.LogInformation("Admin deleted series {SeriesId} {Name}, repointing {StudyDecks} study decks", id, entity.Name, repointed);
        return NoContent();
    }

    [HttpPost("series/{id:int}/members/remove")]
    public async Task<IActionResult> RemoveSeriesMembers(int id, [FromBody] SeriesMembersRequest request, [FromServices] FranchiseSyncRunner franchiseSync)
    {
        if (!await dbContext.Series.AnyAsync(s => s.SeriesId == id))
            return NotFound();

        var deckIds = request.DeckIds.Distinct().ToList();
        var removed = await dbContext.SeriesMembers.Where(m => m.SeriesId == id && deckIds.Contains(m.DeckId)).ExecuteDeleteAsync();

        if (removed > 0)
            await franchiseSync.RunAsync();

        return Ok(new { removed });
    }

    [HttpPost("series/{id:int}/merge-into/{targetId:int}")]
    public async Task<IActionResult> MergeSeries(int id, int targetId, [FromServices] SeriesService series,
                                                 [FromServices] FranchiseSyncRunner franchiseSync)
    {
        if (id == targetId)
            return BadRequest(new { Message = "A series cannot be merged into itself." });

        var source = await dbContext.Series.FirstOrDefaultAsync(s => s.SeriesId == id);
        var target = await dbContext.Series.FirstOrDefaultAsync(s => s.SeriesId == targetId);
        if (source == null || target == null)
            return NotFound();
        if (source.Kind != target.Kind)
            return BadRequest(new { Message = "Only groups of the same kind can be merged." });

        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var targetDecks = await dbContext.SeriesMembers.Where(m => m.SeriesId == targetId).Select(m => m.DeckId).ToListAsync();
        var moving = await dbContext.SeriesMembers.Where(m => m.SeriesId == id && !targetDecks.Contains(m.DeckId)).Select(m => m.DeckId).ToListAsync();
        dbContext.SeriesMembers.AddRange(moving.Select(deckId => new SeriesMember { SeriesId = targetId, DeckId = deckId }));
        await dbContext.SaveChangesAsync();
        await dbContext.SeriesMembers.Where(m => m.SeriesId == id).ExecuteDeleteAsync();
        dbContext.Series.Remove(source);
        target.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        var repointed = await FranchiseSync.RepointStudyDecksAsync(userContext, MediaGroupKind.Series, id, targetId);
        await franchiseSync.RunAsync();

        logger.LogInformation("Admin merged series {SourceId} into {TargetId}, repointing {StudyDecks} study decks", id, targetId, repointed);
        return Ok(await series.ToRefAsync(target.SeriesId));
    }

    [HttpGet("franchise-builder/{deckId:int}")]
    public async Task<IActionResult> GetFranchiseBuilder(int deckId, [FromServices] FranchiseService franchise)
    {
        var dto = await franchise.BuildForDeckAsync(deckId);
        return dto == null ? NotFound() : Ok(dto);
    }

    [HttpGet("franchise-builder/decks")]
    public async Task<IActionResult> GetFranchiseBuilderDecks([FromQuery] List<int> ids, [FromServices] FranchiseService franchise)
    {
        if (ids.Count > FranchiseSuggestionService.MaxDismissDecks)
            return BadRequest(new { Message = $"Ask for at most {FranchiseSuggestionService.MaxDismissDecks} decks." });

        return Ok(await franchise.LoadNodesAsync(ids.Distinct().ToList()));
    }

    [HttpPost("franchise-builder/save")]
    public async Task<IActionResult> SaveFranchiseBuilder([FromBody] FranchiseBuilderSaveRequest request,
                                                          [FromServices] FranchiseService franchise,
                                                          [FromServices] FranchiseSyncRunner franchiseSync)
    {
        foreach (var edge in request.AddEdges.Concat(request.RemoveEdges))
            if (DeckRelationship.ValidateEdge(edge.SourceDeckId, edge.TargetDeckId, edge.RelationshipType) is { } edgeError)
                return BadRequest(new { Message = edgeError });

        var deckIds = request.AddEdges.Concat(request.RemoveEdges).SelectMany(e => new[] { e.SourceDeckId, e.TargetDeckId })
                             .Concat(request.AddMembers.Concat(request.RemoveMembers).Select(m => m.DeckId))
                             .Append(request.AnchorDeckId)
                             .Distinct()
                             .ToList();
        var knownDecks = await dbContext.Decks.AsNoTracking().Where(d => deckIds.Contains(d.DeckId)).Select(d => d.DeckId).ToListAsync();
        var missingDeck = deckIds.Except(knownDecks).FirstOrDefault(-1);
        if (missingDeck != -1)
            return BadRequest(new { Message = $"Unknown deck id {missingDeck}." });

        var seriesIds = request.AddMembers.Concat(request.RemoveMembers).Select(m => m.SeriesId).Distinct().ToList();
        var knownSeries = await dbContext.Series.AsNoTracking().Where(s => seriesIds.Contains(s.SeriesId)).Select(s => s.SeriesId).ToListAsync();
        var missingSeries = seriesIds.Except(knownSeries).FirstOrDefault(-1);
        if (missingSeries != -1)
            return BadRequest(new { Message = $"Unknown series id {missingSeries}." });

        var edgeDeckIds = request.AddEdges.Concat(request.RemoveEdges).SelectMany(e => new[] { e.SourceDeckId, e.TargetDeckId }).Distinct().ToList();
        var memberDeckIds = request.AddMembers.Concat(request.RemoveMembers).Select(m => m.DeckId).Distinct().ToList();

        await using var transaction = await dbContext.Database.BeginTransactionAsync();

        var existingEdges = await dbContext.DeckRelationships
                                           .Where(r => edgeDeckIds.Contains(r.SourceDeckId) && edgeDeckIds.Contains(r.TargetDeckId))
                                           .ToListAsync();
        var removeEdgeKeys = request.RemoveEdges.Select(e => (e.SourceDeckId, e.TargetDeckId, e.RelationshipType)).ToHashSet();
        var removedEdges = existingEdges.Where(r => removeEdgeKeys.Contains((r.SourceDeckId, r.TargetDeckId, r.RelationshipType))).ToList();
        dbContext.DeckRelationships.RemoveRange(removedEdges);
        await dbContext.SaveChangesAsync();

        var presentEdges = existingEdges.Except(removedEdges).Select(r => (r.SourceDeckId, r.TargetDeckId, r.RelationshipType)).ToHashSet();
        var addedEdges = request.AddEdges
                                .Where(e => !presentEdges.Contains((e.TargetDeckId, e.SourceDeckId, DeckRelationship.GetInverse(e.RelationshipType))) &&
                                            presentEdges.Add((e.SourceDeckId, e.TargetDeckId, e.RelationshipType)))
                                .ToList();
        dbContext.DeckRelationships.AddRange(addedEdges.Select(e => new DeckRelationship
        {
            SourceDeckId = e.SourceDeckId,
            TargetDeckId = e.TargetDeckId,
            RelationshipType = e.RelationshipType
        }));
        await dbContext.SaveChangesAsync();

        foreach (var edge in addedEdges)
        {
            if (DeckRelationship.StoryFlow(edge.RelationshipType, edge.SourceDeckId, edge.TargetDeckId) is not (var earlier, var later))
                continue;

            var path = await FindStoryFlowPathAsync(later, earlier);
            if (path == null)
                continue;

            await transaction.RollbackAsync();
            var titles = await dbContext.Decks.AsNoTracking()
                                        .Where(d => path.Contains(d.DeckId))
                                        .ToDictionaryAsync(d => d.DeckId, d => d.OriginalTitle);
            var cycle = new[] { earlier }.Concat(path).Select(id => $"{titles.GetValueOrDefault(id, "?")} ({id})");
            return Conflict(new { Message = $"These links would form a cycle: {string.Join(" -> ", cycle)}." });
        }

        var existingMembers = await dbContext.SeriesMembers
                                             .Where(m => seriesIds.Contains(m.SeriesId) && memberDeckIds.Contains(m.DeckId))
                                             .ToListAsync();
        var removeMemberKeys = request.RemoveMembers.Select(m => (m.SeriesId, m.DeckId)).ToHashSet();
        var removedMembers = existingMembers.Where(m => removeMemberKeys.Contains((m.SeriesId, m.DeckId))).ToList();
        dbContext.SeriesMembers.RemoveRange(removedMembers);
        await dbContext.SaveChangesAsync();

        var presentMembers = existingMembers.Except(removedMembers).Select(m => (m.SeriesId, m.DeckId)).ToHashSet();
        var addedMembers = request.AddMembers.Select(m => (m.SeriesId, m.DeckId)).Where(presentMembers.Add).ToList();
        dbContext.SeriesMembers.AddRange(addedMembers.Select(m => new SeriesMember { SeriesId = m.SeriesId, DeckId = m.DeckId }));
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();

        var touchedSeries = removedMembers.Select(m => m.SeriesId).Concat(addedMembers.Select(m => m.SeriesId)).Distinct().Count();

        await franchiseSync.RunAsync();

        logger.LogInformation("Admin saved franchise builder for deck {DeckId}: +{AddEdges}/-{RemoveEdges} edges, {Members} series touched",
                              request.AnchorDeckId, addedEdges.Count, removedEdges.Count, touchedSeries);

        return Ok(await franchise.BuildForDeckAsync(request.AnchorDeckId));
    }

    /// <summary>Path from <paramref name="from"/> to <paramref name="to"/> along story flow (earlier -> later) over every directed type, both ends included; null when none.</summary>
    private async Task<List<int>?> FindStoryFlowPathAsync(int from, int to)
    {
        var previous = new Dictionary<int, int> { [from] = from };
        var frontier = new List<int> { from };

        while (frontier.Count > 0 && previous.Count < MaxCycleSearchNodes)
        {
            var current = frontier;
            var edges = await dbContext.DeckRelationships.AsNoTracking()
                                       .Where(r => current.Contains(r.SourceDeckId) || current.Contains(r.TargetDeckId))
                                       .Where(DeckRelationship.IsStoryLink)
                                       .Select(r => new { r.SourceDeckId, r.TargetDeckId, r.RelationshipType })
                                       .ToListAsync();

            var currentSet = current.ToHashSet();
            frontier = [];
            foreach (var e in edges)
            {
                if (DeckRelationship.StoryFlow(e.RelationshipType, e.SourceDeckId, e.TargetDeckId) is not (var earlier, var later) ||
                    !currentSet.Contains(earlier) || !previous.TryAdd(later, earlier))
                    continue;

                if (later == to)
                {
                    var path = new List<int> { to };
                    for (var node = to; node != from; node = previous[node])
                        path.Add(previous[node]);
                    path.Reverse();
                    return path;
                }

                frontier.Add(later);
            }
        }

        return null;
    }
}
