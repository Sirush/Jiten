using Jiten.Core;
using Jiten.Core.Data.CommunityLists;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public interface ICommunityListActivityBuffer : IActivityBuffer
{
    void RecordView(int listId, string visitorKey);
}

public class CommunityListActivityBuffer(IDbContextFactory<JitenDbContext> contextFactory, ILogger<CommunityListActivityBuffer> logger)
    : DailyCountBuffer<int>(logger), ICommunityListActivityBuffer
{
    public void RecordView(int listId, string visitorKey)
    {
        if (Visitors.TryAdmit(listId, visitorKey))
            Add(listId, 1);
    }

    protected override int Merge(int current, int added) => current + added;

    protected override async Task WriteAsync(List<KeyValuePair<(int EntityId, DateOnly Date), int>> batch, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var listIds = batch.Select(b => b.Key.EntityId).Distinct().ToList();
        var knownLists = await context.CommunityLists.Where(l => listIds.Contains(l.ListId)).Select(l => l.ListId).ToHashSetAsync(cancellationToken);
        var dates = batch.Select(b => b.Key.Date).Distinct().ToList();
        var existing = await context.CommunityListActivityDailies
                                    .Where(a => listIds.Contains(a.ListId) && dates.Contains(a.Date))
                                    .ToDictionaryAsync(a => (a.ListId, a.Date), cancellationToken);

        foreach (var (key, views) in batch)
        {
            if (!knownLists.Contains(key.EntityId)) continue;
            if (existing.TryGetValue((key.EntityId, key.Date), out var row))
                row.Views += views;
            else
                context.CommunityListActivityDailies.Add(new CommunityListActivityDaily { ListId = key.EntityId, Date = key.Date, Views = views });
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
