using Jiten.Api.Dtos;
using Jiten.Api.Enums;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>
/// Members, stats and vocabulary of a franchise, series or line. <c>kind</c> 1 franchise, 2 series, 3 line; <c>id</c> is the franchise
/// id, series id, or the line's anchor deck id; <c>mediaTypes</c> and <c>excludeDeckIds</c> are comma-separated.
/// </summary>
[ApiController]
[Route("api/media-group")]
[EnableRateLimiting("fixed")]
[Produces("application/json")]
[SwaggerTag("Media groups")]
public class MediaGroupController(
    JitenDbContext context,
    ICurrentUserService currentUserService,
    IFrequencySourceResolver frequencySourceResolver,
    MediaGroupService mediaGroupService,
    FranchiseService franchiseService) : ControllerBase
{
    /// <summary>Every deck of the group, filters not applied.</summary>
    [HttpGet("members")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Client, VaryByHeader = "Authorization")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Get the members of a media group")]
    [ProducesResponseType(typeof(MediaGroupMembersDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MediaGroupMembersDto>> GetMembers(MediaGroupKind kind, int id)
    {
        if (await mediaGroupService.DescribeAsync(new MediaGroupRef(kind, id)) is not { } group)
            return NotFound();

        var members = (await franchiseService.LoadNodesAsync(group.DeckIds))
                      .OrderBy(n => FranchiseNaming.ReleaseOrder(DateOnly.FromDateTime(n.ReleaseDate)))
                      .ThenBy(n => n.DeckId)
                      .ToList();

        // Per-user data must not be shared from the response cache.
        if (currentUserService.IsAuthenticated && members.Count > 0)
        {
            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            await franchiseService.ApplyCoverageAsync(members, currentUserService.UserId!);
        }

        return new MediaGroupMembersDto { Kind = kind, Id = id, Name = group.Name, FranchiseId = group.FranchiseId, Members = members };
    }

    /// <summary>Live totals of the filtered deck set.</summary>
    [HttpGet("stats")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Client)]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Get the stats of a media group")]
    [ProducesResponseType(typeof(MediaGroupStatsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MediaGroupStatsDto>> GetStats(MediaGroupKind kind, int id, string? mediaTypes = null,
                                                                 string? excludeDeckIds = null)
    {
        var deckIds = await ResolveAsync(kind, id, mediaTypes, excludeDeckIds);
        if (deckIds == null)
            return NotFound();

        return await mediaGroupService.GetStatsAsync(deckIds);
    }

    /// <summary>
    /// Vocabulary merged across the filtered decks, occurrences summed; same parameters and shape as the deck vocabulary endpoint,
    /// with <c>deck</c> and <c>parentDeck</c> left null. An unknown group returns an empty page.
    /// </summary>
    [HttpGet("vocabulary")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Get media group vocabulary")]
    [ProducesResponseType(typeof(PaginatedResponse<DeckVocabularyListDto?>), StatusCodes.Status200OK)]
    public async Task<PaginatedResponse<DeckVocabularyListDto?>> GetVocabulary(MediaGroupKind kind, int id, string? sortBy = "",
                                                                               SortOrder sortOrder = SortOrder.Ascending,
                                                                               int? offset = 0, string displayFilter = "all",
                                                                               string? suspended = null, string? redundant = null,
                                                                               string? search = null,
                                                                               string? pos = null, string? excludePos = null,
                                                                               bool hideKanaOnly = false,
                                                                               int limit = 100,
                                                                               MediaType? frequencySource = null,
                                                                               string? mediaTypes = null,
                                                                               string? excludeDeckIds = null)
    {
        int pageSize = Math.Clamp(limit, 1, 200);
        int skip = Math.Max(offset ?? 0, 0);

        var deckIds = await ResolveAsync(kind, id, mediaTypes, excludeDeckIds);
        if (deckIds == null)
            return new PaginatedResponse<DeckVocabularyListDto?>(null, 0, pageSize, skip);

        frequencySource ??= (await frequencySourceResolver.Resolve()).MediaType;

        var source = await VocabularyFilterHelper.ApplyWordFilters(context, context.DeckWords.AsNoTracking().Where(dw => deckIds.Contains(dw.DeckId)),
                                                                   search, pos, excludePos, hideKanaOnly);
        var (page, totalCount) = await VocabularyFilterHelper.PageAsync(context, currentUserService, MediaGroupStats.MergeWords(source, context.Decks),
                                                                        VocabularyDisplayFilter.Parse(displayFilter, suspended, redundant),
                                                                        sortBy, sortOrder, frequencySource, skip, pageSize);

        var dto = new DeckVocabularyListDto { ParentDeck = null, Deck = null, AppliedFrequencySource = frequencySource };
        dto.Words = await VocabularyWordListBuilder.BuildAsync(context, frequencySourceResolver, currentUserService,
                                                               page.Select(w => new VocabularyRow(w.WordId, w.ReadingIndex, w.Occurrences)).ToList(),
                                                               frequencySource);

        return new PaginatedResponse<DeckVocabularyListDto?>(dto, totalCount, pageSize, skip);
    }

    private Task<List<int>?> ResolveAsync(MediaGroupKind kind, int id, string? mediaTypes, string? excludeDeckIds) =>
        mediaGroupService.ResolveDeckIdsAsync(new MediaGroupRef(kind, id),
                                              ParseList(mediaTypes, v => Enum.IsDefined((MediaType)v) ? (MediaType?)v : null),
                                              ParseList(excludeDeckIds, v => (int?)v));

    private static List<T>? ParseList<T>(string? csv, Func<int, T?> map) where T : struct
    {
        if (string.IsNullOrWhiteSpace(csv))
            return null;

        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(part => int.TryParse(part, out var v) ? map(v) : null)
                  .Where(v => v.HasValue)
                  .Select(v => v!.Value)
                  .Distinct()
                  .ToList();
    }
}
