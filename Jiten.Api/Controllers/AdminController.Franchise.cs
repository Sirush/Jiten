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
            filtered = filtered.Where(f => f.Name.ToLower().Contains(needle) ||
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
        franchise.NameTitles is not { } titles
            ? franchise.Name
            : language switch
            {
                TitleLanguage.Romaji => titles.RomajiTitle ?? titles.OriginalTitle,
                TitleLanguage.English => titles.EnglishTitle ?? titles.RomajiTitle ?? titles.OriginalTitle,
                _ => titles.OriginalTitle
            };

    /// <summary>A non-empty name becomes manual and survives syncs; an empty one returns the franchise to its automatic name.</summary>
    [HttpPatch("franchise/{id:int}")]
    public async Task<IActionResult> UpdateFranchise(int id, [FromBody] UpdateFranchiseRequest request,
                                                     [FromServices] FranchiseSyncRunner franchiseSync,
                                                     [FromServices] FranchiseResponseCache responseCache)
    {
        var entity = await dbContext.Franchises.FirstOrDefaultAsync(f => f.FranchiseId == id);
        if (entity == null)
            return NotFound();

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            entity.NameIsManual = false;
            entity.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
            await franchiseSync.RunAsync();
        }
        else
        {
            if (SeriesService.ValidateName(name) is { } nameError)
                return BadRequest(new { Message = nameError });

            entity.Name = name;
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
            Name = f.Name,
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

        var nameDecks = (await franchises.Join(dbContext.Decks, f => (int?)f.FranchiseId, d => d.FranchiseId, (f, d) => new { f, d })
                                         .Where(x => x.d.OriginalTitle == x.f.Name)
                                         .Select(x => new { x.f.FranchiseId, x.d.DeckId, x.d.OriginalTitle, x.d.RomajiTitle, x.d.EnglishTitle })
                                         .ToListAsync())
                        .GroupBy(d => d.FranchiseId)
                        .ToDictionary(g => g.Key, g => g.MinBy(d => d.DeckId)!);

        foreach (var summary in summaries)
        {
            summary.SeriesCount = seriesCounts.GetValueOrDefault(summary.FranchiseId);
            if (deckStats.TryGetValue(summary.FranchiseId, out var stats))
            {
                summary.DeckCount = stats.Count;
                summary.FirstDeckId = stats.FirstDeckId;
            }
            if (nameDecks.TryGetValue(summary.FranchiseId, out var deck))
                summary.NameTitles = new MediaGroupTitlesDto
                {
                    OriginalTitle = deck.OriginalTitle,
                    RomajiTitle = string.IsNullOrWhiteSpace(deck.RomajiTitle) ? null : deck.RomajiTitle,
                    EnglishTitle = string.IsNullOrWhiteSpace(deck.EnglishTitle) ? null : deck.EnglishTitle
                };
        }

        return summaries;
    }

    [HttpPost("franchise/sync")]
    public async Task<FranchiseSyncSummary> SyncFranchises([FromServices] FranchiseSyncRunner franchiseSync) =>
        await franchiseSync.RunAsync();
}
