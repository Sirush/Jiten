using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

[Route("api/stats")]
[EnableRateLimiting("fixed")]
public class StatsController(JitenDbContext context) : ControllerBase
{
    [HttpGet("get-global-stats")]
    [ResponseCache(Duration = 60 * 60 * 24)]
    public async Task<GlobalStatsDto> GetGlobalStats()
    {
        Dictionary<MediaType, int> mediaByType = new();
        var decks = context.Decks.AsNoTracking().Where(d => d.ParentDeckId == null);
        foreach (var mediaType in Enum.GetValues<MediaType>())
        {
            mediaByType.Add(mediaType, await decks.CountAsync(d => d.MediaType == mediaType));
        }

        var totalMojis = await decks.SumAsync(d => d.CharacterCount);
        var totalMedias = mediaByType.Values.Sum();

        return new GlobalStatsDto { MediaByType = mediaByType, TotalMojis = totalMojis, TotalMedia = totalMedias };
    }
}