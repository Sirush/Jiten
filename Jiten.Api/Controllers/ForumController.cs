using Jiten.Api.Dtos;
using Jiten.Api.Services.Forum;
using Jiten.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jiten.Api.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/forum")]
[EnableRateLimiting("fixed")]
public class ForumController(JitenDbContext context, IForumDiscussionService forum, IWebHostEnvironment environment) : ControllerBase
{
    [HttpGet("discussions/{subject}/{id:int}")]
    [AllowAnonymous]
    public async Task<ActionResult<ForumDiscussionDto>> GetDiscussion(string subject, int id, [FromQuery] int? before,
                                                                      [FromQuery] int? previewTopic, CancellationToken ct)
    {
        if (previewTopic != null && environment.IsDevelopment())
            return await forum.GetByTopicIdAsync(previewTopic.Value, before, ct);

        var embedUrl = await ForumDiscussionSubjects.EmbedUrlAsync(context, subject, id, ct);
        if (embedUrl == null) return NotFound();
        return await forum.GetAsync(embedUrl, before, ct);
    }

    /// <summary>Signed-in only, so crawlers and anonymous scripts can't make the forum create a topic for every page.</summary>
    [HttpPost("discussions/{subject}/{id:int}/start")]
    [Authorize]
    [EnableRateLimiting("forum-start")]
    public async Task<ActionResult<ForumDiscussionDto>> StartDiscussion(string subject, int id, CancellationToken ct)
    {
        var embedUrl = await ForumDiscussionSubjects.EmbedUrlAsync(context, subject, id, ct);
        if (embedUrl == null) return NotFound();
        return await forum.StartAsync(embedUrl, ct);
    }
}
