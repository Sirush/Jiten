using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/franchise")]
[EnableRateLimiting("fixed")]
[Produces("application/json")]
[SwaggerTag("Franchises")]
public class FranchiseController(FranchiseService franchiseService, ICurrentUserService currentUserService) : ControllerBase
{
    /// <summary>Every deck of the franchise with the story links between them, its lines, series and shared settings.</summary>
    [HttpGet("{franchiseId:int}")]
    [ResponseCache(Duration = 300, Location = ResponseCacheLocation.Client, VaryByHeader = "Authorization")]
    [AllowAnonymous]
    [SwaggerOperation(Summary = "Get a franchise")]
    [ProducesResponseType(typeof(FranchiseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FranchiseDto>> GetFranchise(int franchiseId)
    {
        // Per-user data must not be shared from the response cache.
        if (currentUserService.IsAuthenticated)
            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";

        var dto = await franchiseService.GetForViewerAsync(franchiseId, currentUserService.IsAuthenticated ? currentUserService.UserId : null);
        return dto == null ? NotFound() : dto;
    }
}
