using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>
/// Provides endpoints that expose global statistics for the Jiten dataset.
/// </summary>
/// <remarks>
/// These endpoints are rate-limited by the "fixed" policy and return JSON payloads suitable for clients and dashboards.
/// </remarks>
[ApiController]
[Route("api/stats")]
[EnableRateLimiting("fixed")]
[Produces("application/json")]
[SwaggerTag("Operations that provide global statistics for the Jiten dataset.")]
public class StatsController(JitenDbContext context) : ControllerBase
{
    /// <summary>
    /// Gets aggregated global statistics.
    /// </summary>
    /// <remarks>
    /// Returns totals such as number of media per <see cref="MediaType"/>, total mojis (characters), and total media.
    /// The response is cached for 24 hours to reduce load.
    /// </remarks>
    /// <response code="200">The aggregated global statistics.</response>
    /// <response code="429">Too many requests due to rate limiting.</response>
    /// <response code="500">An unexpected error occurred on the server.</response>
    /// <returns>A <see cref="GlobalStatsDto"/> describing global counts.</returns>
    [HttpGet("get-global-stats")]
    [ResponseCache(Duration = 60 * 60 * 24)]
    [SwaggerOperation(
        Summary = "Gets aggregated global statistics.",
        Description = "Returns totals such as number of media per type, total mojis, and total media. Results are cached for 24 hours.",
        OperationId = "Stats_GetGlobalStats")]
    [ProducesResponseType(typeof(GlobalStatsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<GlobalStatsDto> GetGlobalStats()
    {
        Dictionary<MediaType, int> mediaByType = new();
        var decks = context.Decks.AsNoTracking().Where(d => d.ParentDeckId == null);
        foreach (var mediaType in MediaTypes.Listed)
        {
            mediaByType.Add(mediaType, await decks.CountAsync(d => d.MediaType == mediaType));
        }

        double totalMojis = await decks.SumAsync(d => (double)d.CharacterCount);
        var totalMedias = mediaByType.Values.Sum();

        return new GlobalStatsDto { MediaByType = mediaByType, TotalMojis = totalMojis, TotalMedia = totalMedias };
    }
}