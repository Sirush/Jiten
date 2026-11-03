using System.Text.Json;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Services;

public readonly record struct GroupedNotificationContent(string Title, string Message, string? LinkUrl);

/// <summary>The only path that creates notifications, so mutes apply to every sender.</summary>
public class NotificationService(JitenDbContext context, UserDbContext userContext)
{
    public async Task Notify(string userId, NotificationType type, string title, string message, string? linkUrl = null)
    {
        if (await IsMutedAsync(userId, type))
            return;

        context.Notifications.Add(new Notification
        {
            UserId = userId,
            Type = type,
            Title = title,
            Message = message,
            LinkUrl = linkUrl
        });
        await context.SaveChangesAsync();
    }

    /// <summary>Folds into the user's unread notification with the same key, moving it to the top; build gets the folded count and the link already on the row.</summary>
    public async Task NotifyGrouped(string userId, NotificationType type, string groupKey,
                                    Func<int, string?, GroupedNotificationContent> build)
    {
        if (await IsMutedAsync(userId, type))
            return;

        var existing = await context.Notifications
                                    .Where(n => n.UserId == userId && n.GroupKey == groupKey && !n.IsRead)
                                    .OrderByDescending(n => n.CreatedAt)
                                    .FirstOrDefaultAsync();
        if (existing == null)
        {
            var content = build(1, null);
            context.Notifications.Add(new Notification
            {
                UserId = userId,
                Type = type,
                Title = content.Title,
                Message = content.Message,
                LinkUrl = content.LinkUrl,
                GroupKey = groupKey
            });
        }
        else
        {
            var content = build(existing.GroupCount + 1, existing.LinkUrl);
            existing.GroupCount++;
            existing.Title = content.Title;
            existing.Message = content.Message;
            existing.LinkUrl = content.LinkUrl;
            existing.CreatedAt = DateTime.UtcNow;
        }

        await context.SaveChangesAsync();
    }

    public async Task NotifyMany(IEnumerable<string> userIds, NotificationType type, string title, string message, string? linkUrl = null)
    {
        var recipients = userIds.Distinct().ToList();
        if (NotificationGroups.IsMutable(type) && recipients.Count > 0)
        {
            var muted = await MutedRecipientsAsync(recipients, NotificationGroups.Of(type));
            recipients.RemoveAll(muted.Contains);
        }

        var notifications = recipients.Select(userId => new Notification
                                      {
                                          UserId = userId,
                                          Type = type,
                                          Title = title,
                                          Message = message,
                                          LinkUrl = linkUrl
                                      })
                                      .ToList();

        if (notifications.Count == 0) return;

        // AddRange runs change detection once; per-row Add is quadratic at site-wide broadcast sizes.
        context.Notifications.AddRange(notifications);
        await context.SaveChangesAsync();
    }

    public static List<NotificationGroup> ParseMutedGroups(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return (JsonSerializer.Deserialize<List<int>>(json) ?? [])
                   .Select(g => (NotificationGroup)g)
                   .Where(NotificationGroups.Mutable.Contains)
                   .Distinct()
                   .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<bool> IsMutedAsync(string userId, NotificationType type)
    {
        if (!NotificationGroups.IsMutable(type))
            return false;

        var json = await userContext.UserSettings.AsNoTracking()
                                    .Where(us => us.UserId == userId)
                                    .Select(us => us.MutedNotificationGroupsJson)
                                    .FirstOrDefaultAsync();
        return ParseMutedGroups(json).Contains(NotificationGroups.Of(type));
    }

    private async Task<HashSet<string>> MutedRecipientsAsync(List<string> userIds, NotificationGroup group)
    {
        var rows = await userContext.UserSettings.AsNoTracking()
                                    .Where(us => userIds.Contains(us.UserId))
                                    .Select(us => new { us.UserId, us.MutedNotificationGroupsJson })
                                    .ToListAsync();
        return rows.Where(r => ParseMutedGroups(r.MutedNotificationGroupsJson).Contains(group))
                   .Select(r => r.UserId)
                   .ToHashSet();
    }
}
