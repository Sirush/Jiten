using Jiten.Core;
using Jiten.Parser.Romanization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/utils")]
public class UtilsController(IDbContextFactory<JitenDbContext> contextFactory) : ControllerBase
{
    [HttpPost("romanize")]
    [AllowAnonymous]
    [EnableRateLimiting("fixed")]
    public async Task<IResult> Romanize([FromBody] RomanizeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return Results.BadRequest(new { error = "Title is required" });

        if (request.Title.Length > 100)
            return Results.BadRequest(new { error = "Title must be 100 characters or fewer" });

        return Results.Ok(new { romaji = await TitleRomanizer.RomanizeAsync(contextFactory, request.Title) });
    }
}

public record RomanizeRequest(string Title);
