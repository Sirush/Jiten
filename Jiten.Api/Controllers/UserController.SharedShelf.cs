using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

public partial class UserController
{
    private const int MaxSharedShelfItems = 120;

    /// <summary>
    /// Titles the viewer and a public profile have both read, and titles the profile has read that the viewer has not.
    /// Same access rule as the profile's media list; the profile owner's favourites are private and never affect the response.
    /// </summary>
    [HttpGet("profile/{username}/shared-shelf")]
    [SwaggerOperation(Summary = "Compare the viewer's media list with a public profile's")]
    public async Task<IResult> GetSharedShelf(string username)
    {
        var viewerId = userService.UserId;
        if (string.IsNullOrEmpty(viewerId)) return Results.Unauthorized();

        var (targetId, _, allowed) = await ResolveMediaListAccessAsync(username);
        if (targetId == null || !allowed)
            return Results.NotFound(new { message = "Media list not found" });
        if (targetId == viewerId)
            return Results.BadRequest(new { message = "Open another member's profile to compare lists" });

        var theirs = await MediaListStatusHelper.GetDisplayStatusesAsync(userContext, jitenContext, targetId);
        var yours = await MediaListStatusHelper.GetDisplayStatusesAsync(userContext, jitenContext, viewerId);
        DeckStatus YourStatus(int deckId) => yours.GetValueOrDefault(deckId).Status;

        var candidateIds = theirs.Where(kv => kv.Value.Status.HasStarted() && YourStatus(kv.Key) != DeckStatus.Dropped)
                                 .Select(kv => kv.Key)
                                 .ToList();
        var titles = await jitenContext.Decks
                                       .AsNoTracking()
                                       .Where(d => candidateIds.Contains(d.DeckId))
                                       .ToDictionaryAsync(d => d.DeckId, d => d.OriginalTitle);
        var coverage = await UserCoverageChunkHelper.GetCoverage(userContext, viewerId, titles.Keys);

        var sharedIds = titles.Keys.Where(id => YourStatus(id).HasStarted())
                              .OrderByDescending(id => yours[id].IsFavourite)
                              .ThenBy(id => titles[id])
                              .ToList();
        var theirsOnlyIds = titles.Keys.Where(id => !YourStatus(id).HasStarted())
                                  .OrderByDescending(id => coverage.MatureCoverage[id])
                                  .ThenBy(id => titles[id])
                                  .ToList();

        var pageIds = sharedIds.Take(MaxSharedShelfItems).Concat(theirsOnlyIds.Take(MaxSharedShelfItems)).ToList();
        var cards = await jitenContext.Decks
                                      .AsNoTracking()
                                      .Include(d => d.DeckDifficulty)
                                      .Where(d => pageIds.Contains(d.DeckId))
                                      .ToDictionaryAsync(d => d.DeckId, d => new DeckCardDto(d));
        coverage.ApplyTo(cards.Values);

        List<SharedShelfItemDto> Page(List<int> ids) =>
            ids.Take(MaxSharedShelfItems)
               .Where(cards.ContainsKey)
               .Select(id => new SharedShelfItemDto { Deck = cards[id], YourStatus = YourStatus(id) })
               .ToList();

        return Results.Ok(new SharedShelfDto
                          {
                              ViewerHasList = yours.Count > 0,
                              SharedCount = sharedIds.Count,
                              TheirsOnlyCount = theirsOnlyIds.Count,
                              Shared = Page(sharedIds),
                              TheirsOnly = Page(theirsOnlyIds)
                          });
    }
}
