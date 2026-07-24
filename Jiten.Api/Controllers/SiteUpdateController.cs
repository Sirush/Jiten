using Jiten.Api.Dtos;
using Jiten.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Swashbuckle.AspNetCore.Annotations;

namespace Jiten.Api.Controllers;

/// <summary>Published site updates (changelog). Drafts are never exposed here.</summary>
[ApiController]
[Route("api/updates")]
[EnableRateLimiting("fixed")]
[Produces("application/json")]
[SwaggerTag("Operations that expose the public site update changelog.")]
public class SiteUpdateController(JitenDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<IResult> GetUpdates([FromQuery] int offset = 0, [FromQuery] int limit = 10)
    {
        limit = Math.Clamp(limit, 1, 50);
        offset = Math.Max(offset, 0);

        var query = context.SiteUpdates.AsNoTracking()
                           .Where(u => u.PublishedAt != null);

        var totalCount = await query.CountAsync();

        var updates = await query
                            .OrderByDescending(u => u.PublishedAt)
                            .Skip(offset)
                            .Take(limit)
                            .Select(u => new SiteUpdateDto
                            {
                                Id = u.Id,
                                Title = u.Title,
                                BodyMarkdown = u.BodyMarkdown,
                                PublishedAt = u.PublishedAt!.Value,
                                UpdatedAt = u.UpdatedAt
                            })
                            .ToListAsync();

        return Results.Ok(new PaginatedResponse<List<SiteUpdateDto>>(updates, totalCount, limit, offset));
    }

    [HttpGet("{id:int}")]
    public async Task<IResult> GetUpdate(int id)
    {
        var update = await context.SiteUpdates.AsNoTracking()
                                  .Where(u => u.Id == id && u.PublishedAt != null)
                                  .Select(u => new SiteUpdateDto
                                  {
                                      Id = u.Id,
                                      Title = u.Title,
                                      BodyMarkdown = u.BodyMarkdown,
                                      PublishedAt = u.PublishedAt!.Value,
                                      UpdatedAt = u.UpdatedAt
                                  })
                                  .FirstOrDefaultAsync();

        return update is null ? Results.NotFound() : Results.Ok(update);
    }
}
