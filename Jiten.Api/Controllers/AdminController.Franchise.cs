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
    [HttpGet("franchise")]
    public async Task<PaginatedResponse<List<FranchiseSummaryDto>>> ListFranchises(string? query = null, int offset = 0, int limit = 50,
                                                                                  FranchiseListSort sort = FranchiseListSort.Name,
                                                                                  bool descending = false,
                                                                                  TitleLanguage titleLanguage = TitleLanguage.Original,
                                                                                  bool? manual = null)
    {
        limit = Math.Clamp(limit, 1, 200);
        offset = Math.Max(offset, 0);

        var filtered = dbContext.Franchises.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim().ToLower();
            filtered = filtered.Where(f => f.OriginalTitle.ToLower().Contains(needle) ||
                                           (f.RomajiTitle != null && f.RomajiTitle.ToLower().Contains(needle)) ||
                                           (f.EnglishTitle != null && f.EnglishTitle.ToLower().Contains(needle)) ||
                                           f.Decks.Any(d => d.OriginalTitle.ToLower().Contains(needle) ||
                                                            (d.RomajiTitle != null && d.RomajiTitle.ToLower().Contains(needle)) ||
                                                            (d.EnglishTitle != null && d.EnglishTitle.ToLower().Contains(needle))));
        }

        if (manual is { } isManual)
            filtered = filtered.Where(f => f.NameIsManual == isManual);

        var summaries = await LoadFranchiseSummaries(filtered);
        var nameComparer = new FranchiseNameComparer();
        IOrderedEnumerable<FranchiseSummaryDto> ordered = sort switch
        {
            FranchiseListSort.Decks => descending ? summaries.OrderByDescending(f => f.DeckCount) : summaries.OrderBy(f => f.DeckCount),
            FranchiseListSort.Series => descending ? summaries.OrderByDescending(f => f.SeriesCount) : summaries.OrderBy(f => f.SeriesCount),
            _ => descending
                ? summaries.OrderByDescending(f => DisplayName(f, titleLanguage), nameComparer)
                : summaries.OrderBy(f => DisplayName(f, titleLanguage), nameComparer)
        };
        var page = ordered.ThenBy(f => DisplayName(f, titleLanguage), nameComparer).ThenBy(f => f.FranchiseId)
                          .Skip(offset).Take(limit).ToList();

        return new PaginatedResponse<List<FranchiseSummaryDto>>(page, summaries.Count, limit, offset);
    }

    /// <summary>Mirrors the client's title localisation so the name sort matches what the admin reads.</summary>
    private static string DisplayName(FranchiseSummaryDto franchise, TitleLanguage language) =>
        language switch
        {
            TitleLanguage.Romaji => franchise.RomajiTitle ?? franchise.OriginalTitle,
            TitleLanguage.English => franchise.EnglishTitle ?? franchise.RomajiTitle ?? franchise.OriginalTitle,
            _ => franchise.OriginalTitle
        };

    /// <summary>Non-empty titles become manual and survive syncs; a blank original title returns the franchise to automatic titles.</summary>
    [HttpPatch("franchise/{id:int}")]
    public async Task<IActionResult> UpdateFranchise(int id, [FromBody] UpdateFranchiseRequest request,
                                                     [FromServices] FranchiseSyncRunner franchiseSync,
                                                     [FromServices] FranchiseResponseCache responseCache)
    {
        var entity = await dbContext.Franchises.FirstOrDefaultAsync(f => f.FranchiseId == id);
        if (entity == null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.OriginalTitle))
        {
            entity.NameIsManual = false;
            entity.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
            await franchiseSync.RunAsync();
        }
        else
        {
            if (request.Validate() is { } titleError)
                return BadRequest(new { Message = titleError });

            entity.SetTitles(request.ToTitles());
            entity.NameIsManual = true;
            entity.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
            responseCache.Invalidate();
        }

        logger.LogInformation("Admin renamed franchise {FranchiseId} (manual: {Manual})", id, entity.NameIsManual);

        var summary = (await LoadFranchiseSummaries(dbContext.Franchises.AsNoTracking().Where(f => f.FranchiseId == id))).FirstOrDefault();
        return summary == null ? NotFound() : Ok(summary);
    }

    private async Task<List<FranchiseSummaryDto>> LoadFranchiseSummaries(IQueryable<Franchise> franchises)
    {
        var summaries = await franchises.Select(f => new FranchiseSummaryDto
        {
            FranchiseId = f.FranchiseId,
            OriginalTitle = f.OriginalTitle,
            RomajiTitle = f.RomajiTitle,
            EnglishTitle = f.EnglishTitle,
            NameIsManual = f.NameIsManual
        }).ToListAsync();

        var ids = summaries.Select(f => f.FranchiseId).ToList();
        var deckStats = await dbContext.Decks.AsNoTracking()
                                       .Where(d => d.FranchiseId != null && ids.Contains(d.FranchiseId.Value))
                                       .GroupBy(d => d.FranchiseId!.Value)
                                       .Select(g => new { FranchiseId = g.Key, Count = g.Count(), FirstDeckId = g.Min(d => d.DeckId) })
                                       .ToDictionaryAsync(g => g.FranchiseId);
        var seriesCounts = await dbContext.SeriesMembers.AsNoTracking()
                                          .Where(m => m.Series.Kind == SeriesKind.Series && m.Deck.FranchiseId != null &&
                                                      ids.Contains(m.Deck.FranchiseId.Value))
                                          .Select(m => new { m.SeriesId, m.Deck.FranchiseId })
                                          .Distinct()
                                          .GroupBy(m => m.FranchiseId!.Value)
                                          .Select(g => new { FranchiseId = g.Key, Count = g.Count() })
                                          .ToDictionaryAsync(g => g.FranchiseId, g => g.Count);

        foreach (var summary in summaries)
        {
            summary.SeriesCount = seriesCounts.GetValueOrDefault(summary.FranchiseId);
            if (deckStats.TryGetValue(summary.FranchiseId, out var stats))
            {
                summary.DeckCount = stats.Count;
                summary.FirstDeckId = stats.FirstDeckId;
            }
        }

        return summaries;
    }

    [HttpGet("franchise/suggestions")]
    public Task<PaginatedResponse<List<FranchiseSuggestionDto>>> ListFranchiseSuggestions([FromServices] FranchiseSuggestionService suggestions,
                                                                                         string? query = null, MediaType? mediaType = null,
                                                                                         FranchiseSuggestionScope scope = FranchiseSuggestionScope.All,
                                                                                         int offset = 0, int limit = 25) =>
        suggestions.ListAsync(query, mediaType, scope, Math.Max(offset, 0), Math.Clamp(limit, 1, 100));

    [HttpPost("franchise/suggestions/dismiss")]
    public async Task<IActionResult> DismissFranchiseSuggestion([FromBody] FranchiseSuggestionDismissRequest request,
                                                                [FromServices] FranchiseSuggestionService suggestions)
    {
        if (ValidateSuggestionRequest(request) is { } error)
            return BadRequest(new { Message = error });

        var dismissed = await suggestions.DismissAsync(request.RootKey, request.DeckIds.Distinct().ToList());
        logger.LogInformation("Admin dismissed franchise suggestion {RootKey} for {Decks} decks", request.RootKey, dismissed);
        return Ok(new { dismissed });
    }

    [HttpPost("franchise/suggestions/restore")]
    public async Task<IActionResult> RestoreFranchiseSuggestion([FromBody] FranchiseSuggestionDismissRequest request,
                                                                [FromServices] FranchiseSuggestionService suggestions)
    {
        if (ValidateSuggestionRequest(request) is { } error)
            return BadRequest(new { Message = error });

        var restored = await suggestions.RestoreAsync(request.RootKey, request.DeckIds.Distinct().ToList());
        return Ok(new { restored });
    }

    private static string? ValidateSuggestionRequest(FranchiseSuggestionDismissRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RootKey) || request.RootKey.Length > FranchiseNaming.MaxNameLength)
            return "The title root is missing or too long.";
        if (request.DeckIds.Count == 0 || request.DeckIds.Count > FranchiseSuggestionService.MaxDismissDecks)
            return $"Send between 1 and {FranchiseSuggestionService.MaxDismissDecks} decks.";
        return null;
    }

    [HttpPost("franchise/sync")]
    public async Task<FranchiseSyncSummary> SyncFranchises([FromServices] FranchiseSyncRunner franchiseSync) =>
        await franchiseSync.RunAsync();
}
