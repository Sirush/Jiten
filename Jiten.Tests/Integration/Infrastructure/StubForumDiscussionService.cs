using Jiten.Api.Dtos;
using Jiten.Api.Services.Forum;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

/// <summary>Returns the requested embed URL as the topic URL so tests can see which page a subject maps to.</summary>
public class StubForumDiscussionService : IForumDiscussionService
{
    /// <summary>Topic id and reply count a read of an embed URL reports; other URLs report no topic id.</summary>
    public Dictionary<string, (int TopicId, int ReplyCount)> Topics { get; } = [];

    public Task<ForumDiscussionDto> GetAsync(string embedUrl, int? before, CancellationToken ct)
    {
        var found = Topics.TryGetValue(embedUrl, out var topic);
        return Task.FromResult(new ForumDiscussionDto
        {
            Status = ForumDiscussionStatus.Ready, TopicUrl = embedUrl, TopicId = found ? topic.TopicId : null, ReplyCount = topic.ReplyCount
        });
    }

    public Task<ForumDiscussionDto> StartAsync(ForumTopicDraft draft, CancellationToken ct) =>
        Task.FromResult(new ForumDiscussionDto { Status = ForumDiscussionStatus.Ready, TopicUrl = draft.EmbedUrl });

    public Task<ForumDiscussionDto> GetByTopicIdAsync(int topicId, int? before, CancellationToken ct) =>
        Task.FromResult(new ForumDiscussionDto { Status = ForumDiscussionStatus.Ready, TopicUrl = $"topic:{topicId}" });

    public Task<ForumDiscussionDto> RefreshTopicAsync(int topicId, CancellationToken ct) =>
        Task.FromResult(new ForumDiscussionDto { Status = ForumDiscussionStatus.Ready, TopicId = topicId, ReplyCount = RefreshedReplyCount });

    /// <summary>Reply count a webhook-triggered re-read reports.</summary>
    public int RefreshedReplyCount { get; set; }
}
