using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Forum;

public static class ForumTopicStore
{
    /// <summary>Writes only on change, since every discussion read records what it saw; losing a concurrent first insert is harmless.</summary>
    public static async Task RecordAsync(JitenDbContext context, ForumSubject subject, int subjectId, int topicId, int replyCount, CancellationToken ct)
    {
        var row = await context.ForumTopics.FirstOrDefaultAsync(t => t.Subject == subject && t.SubjectId == subjectId, ct);
        if (row is not null && row.TopicId == topicId && row.ReplyCount == replyCount) return;

        var inserting = row is null;
        if (row is null)
        {
            row = new ForumTopic { Subject = subject, SubjectId = subjectId };
            context.ForumTopics.Add(row);
        }

        row.TopicId = topicId;
        row.ReplyCount = replyCount;
        row.UpdatedAt = DateTime.UtcNow;

        try
        {
            await context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (inserting && ex.IsUniqueViolation())
        {
            context.ChangeTracker.Clear();
        }
    }

    public static Task UpdateReplyCountAsync(JitenDbContext context, int topicId, int replyCount, CancellationToken ct) =>
        context.ForumTopics.Where(t => t.TopicId == topicId && t.ReplyCount != replyCount)
               .ExecuteUpdateAsync(s => s.SetProperty(t => t.ReplyCount, replyCount).SetProperty(t => t.UpdatedAt, DateTime.UtcNow), ct);

    public static Task RemoveAsync(JitenDbContext context, int topicId, CancellationToken ct) =>
        context.ForumTopics.Where(t => t.TopicId == topicId).ExecuteDeleteAsync(ct);

    public static Task<int> ReplyCountAsync(JitenDbContext context, ForumSubject subject, int subjectId) =>
        context.ForumTopics.Where(t => t.Subject == subject && t.SubjectId == subjectId).Select(t => t.ReplyCount).FirstOrDefaultAsync();

    public static async Task<Dictionary<int, int>> ReplyCountsAsync(JitenDbContext context, ForumSubject subject, IReadOnlyCollection<int> subjectIds) =>
        subjectIds.Count == 0
            ? []
            : await context.ForumTopics.AsNoTracking()
                           .Where(t => t.Subject == subject && subjectIds.Contains(t.SubjectId) && t.ReplyCount > 0)
                           .ToDictionaryAsync(t => t.SubjectId, t => t.ReplyCount);
}
