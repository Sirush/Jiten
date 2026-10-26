using Jiten.Api.Dtos;
using Jiten.Api.Services.Forum;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

/// <summary>Returns the requested embed URL as the topic URL so tests can see which page a subject maps to.</summary>
public class StubForumDiscussionService : IForumDiscussionService
{
    public Task<ForumDiscussionDto> GetAsync(string embedUrl, int? before, CancellationToken ct) =>
        Task.FromResult(new ForumDiscussionDto { Status = ForumDiscussionStatus.Ready, TopicUrl = embedUrl });

    public Task<ForumDiscussionDto> StartAsync(string embedUrl, CancellationToken ct) =>
        Task.FromResult(new ForumDiscussionDto { Status = ForumDiscussionStatus.Pending, TopicUrl = embedUrl });

    public Task<ForumDiscussionDto> GetByTopicIdAsync(int topicId, int? before, CancellationToken ct) =>
        Task.FromResult(new ForumDiscussionDto { Status = ForumDiscussionStatus.Ready, TopicUrl = $"topic:{topicId}" });
}
