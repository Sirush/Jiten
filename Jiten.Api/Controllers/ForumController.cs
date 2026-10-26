using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Api.Services.Forum;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/forum")]
[EnableRateLimiting("fixed")]
public class ForumController(
    JitenDbContext context,
    IForumDiscussionService forum,
    IOptions<DiscourseOptions> options,
    IWebHostEnvironment environment) : ControllerBase
{
    [HttpGet("discussions/{subject}/{id:int}")]
    [AllowAnonymous]
    public async Task<ActionResult<ForumDiscussionDto>> GetDiscussion(string subject, int id, [FromQuery] int? before,
                                                                      [FromQuery] int? previewTopic, CancellationToken ct)
    {
        if (previewTopic != null && environment.IsDevelopment())
            return await forum.GetByTopicIdAsync(previewTopic.Value, before, ct);

        if (ForumDiscussionSubjects.Parse(subject) is not { } kind) return NotFound();
        var embedUrl = await ForumDiscussionSubjects.EmbedUrlAsync(context, kind, id, ct);
        if (embedUrl == null) return NotFound();

        var discussion = await forum.GetAsync(embedUrl, before, ct);
        return before == null ? await RecordAsync(kind, id, discussion, ct) : discussion;
    }

    /// <summary>Signed-in only, so crawlers and anonymous scripts can't make the forum create a topic for every page.</summary>
    [HttpPost("discussions/{subject}/{id:int}/start")]
    [Authorize]
    [EnableRateLimiting("forum-start")]
    public async Task<ActionResult<ForumDiscussionDto>> StartDiscussion(string subject, int id, CancellationToken ct)
    {
        if (ForumDiscussionSubjects.Parse(subject) is not { } kind) return NotFound();
        var draft = await ForumDiscussionSubjects.DraftAsync(context, kind, id, ct);
        if (draft == null) return NotFound();
        return await RecordAsync(kind, id, await forum.StartAsync(draft, ct), ct);
    }

    /// <summary>Forum webhook for post and topic events; only stored topics are re-read, so unrelated forum activity costs no forum request.</summary>
    [HttpPost("webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(options.Value.WebhookSecret)) return NotFound();

        using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();
        if (!DiscourseWebhook.IsSigned(body, Request.Headers["X-Discourse-Event-Signature"], options.Value.WebhookSecret)) return Unauthorized();

        var topicId = DiscourseWebhook.TopicId(body);
        if (topicId == null) return NoContent();

        if (Request.Headers["X-Discourse-Event"] == "topic_destroyed")
        {
            await ForumTopicStore.RemoveAsync(context, topicId.Value, ct);
            return NoContent();
        }

        if (!await context.ForumTopics.AnyAsync(t => t.TopicId == topicId, ct)) return NoContent();

        var topic = await forum.RefreshTopicAsync(topicId.Value, ct);
        if (topic.Status == ForumDiscussionStatus.Ready)
            await ForumTopicStore.UpdateReplyCountAsync(context, topicId.Value, topic.ReplyCount, ct);
        return NoContent();
    }

    private async Task<ForumDiscussionDto> RecordAsync(ForumSubject kind, int id, ForumDiscussionDto discussion, CancellationToken ct)
    {
        if (discussion is { Status: ForumDiscussionStatus.Ready, TopicId: { } topicId })
            await ForumTopicStore.RecordAsync(context, kind, id, topicId, discussion.ReplyCount, ct);
        return discussion;
    }
}
