using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public interface IDeckActivityBuffer : IActivityBuffer
{
    /// <summary>Counts one view per visitor per deck per UTC day; only a salted hash of the visitor lives in memory, dropped at midnight.</summary>
    void RecordView(int deckId, string visitorKey);
    void RecordGuestDownload(int deckId);
}

public class DeckActivityBuffer(IDbContextFactory<JitenDbContext> contextFactory, ILogger<DeckActivityBuffer> logger)
    : DailyCountBuffer<(int Views, int GuestDownloads)>(logger), IDeckActivityBuffer
{
    public void RecordView(int deckId, string visitorKey)
    {
        if (Visitors.TryAdmit(deckId, visitorKey))
            Add(deckId, (1, 0));
    }

    public void RecordGuestDownload(int deckId) => Add(deckId, (0, 1));

    protected override (int Views, int GuestDownloads) Merge((int Views, int GuestDownloads) current, (int Views, int GuestDownloads) added) =>
        (current.Views + added.Views, current.GuestDownloads + added.GuestDownloads);

    protected override async Task WriteAsync(List<KeyValuePair<(int EntityId, DateOnly Date), (int Views, int GuestDownloads)>> batch,
                                             CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var deckIds = batch.Select(b => b.Key.EntityId).Distinct().ToList();
        var knownDecks = await context.Decks.Where(d => deckIds.Contains(d.DeckId)).Select(d => d.DeckId).ToHashSetAsync(cancellationToken);
        var dates = batch.Select(b => b.Key.Date).Distinct().ToList();
        var existing = await context.DeckActivityDailies
                                    .Where(a => deckIds.Contains(a.DeckId) && dates.Contains(a.Date))
                                    .ToDictionaryAsync(a => (a.DeckId, a.Date), cancellationToken);

        foreach (var (key, counts) in batch)
        {
            if (!knownDecks.Contains(key.EntityId)) continue;
            if (existing.TryGetValue((key.EntityId, key.Date), out var row))
            {
                row.Views += counts.Views;
                row.GuestDownloads += counts.GuestDownloads;
            }
            else
            {
                context.DeckActivityDailies.Add(new DeckActivityDaily
                {
                    DeckId = key.EntityId, Date = key.Date, Views = counts.Views, GuestDownloads = counts.GuestDownloads
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}
