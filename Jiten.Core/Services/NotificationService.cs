using Jiten.Core.Data;

namespace Jiten.Core.Services;

public class NotificationService(JitenDbContext context)
{
    public async Task Notify(string userId, NotificationType type, string title, string message, string? linkUrl = null)
    {
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

    public async Task NotifyMany(IEnumerable<string> userIds, NotificationType type, string title, string message, string? linkUrl = null)
    {
        var notifications = userIds.Distinct()
                                   .Select(userId => new Notification
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
}
