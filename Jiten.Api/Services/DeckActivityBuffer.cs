using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public interface IDeckActivityBuffer
{
    /// <summary>Counts one view per visitor per deck per UTC day; only a salted hash of the visitor lives in memory, dropped at midnight.</summary>
    void RecordView(int deckId, string visitorKey);
    void RecordGuestDownload(int deckId);
    Task FlushAsync(CancellationToken cancellationToken = default);
}

public class DeckActivityBuffer(IDbContextFactory<JitenDbContext> contextFactory, ILogger<DeckActivityBuffer> logger) : IDeckActivityBuffer
{
    private readonly ConcurrentDictionary<(int DeckId, DateOnly Date), (int Views, int GuestDownloads)> _pending = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly object _dayLock = new();
    private DateOnly _visitorDay = DateOnly.FromDateTime(DateTime.UtcNow);
    private byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private ConcurrentDictionary<int, ConcurrentDictionary<ulong, byte>> _seenToday = new();

    public void RecordView(int deckId, string visitorKey)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != _visitorDay)
        {
            lock (_dayLock)
            {
                if (today != _visitorDay)
                {
                    _visitorDay = today;
                    _salt = RandomNumberGenerator.GetBytes(32);
                    _seenToday = new ConcurrentDictionary<int, ConcurrentDictionary<ulong, byte>>();
                }
            }
        }

        var visitor = VisitorHash(visitorKey);
        var seen = _seenToday.GetOrAdd(deckId, _ => new ConcurrentDictionary<ulong, byte>());
        if (seen.TryAdd(visitor, 0))
            Add(deckId, 1, 0);
    }

    private ulong VisitorHash(string visitorKey)
    {
        var input = new byte[_salt.Length + Encoding.UTF8.GetByteCount(visitorKey)];
        _salt.CopyTo(input, 0);
        Encoding.UTF8.GetBytes(visitorKey, 0, visitorKey.Length, input, _salt.Length);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return BitConverter.ToUInt64(digest);
    }

    public void RecordGuestDownload(int deckId) => Add(deckId, 0, 1);

    private void Add(int deckId, int views, int guestDownloads)
    {
        var key = (deckId, DateOnly.FromDateTime(DateTime.UtcNow));
        _pending.AddOrUpdate(key, (views, guestDownloads), (_, c) => (c.Views + views, c.GuestDownloads + guestDownloads));
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_pending.IsEmpty) return;
        await _flushLock.WaitAsync(cancellationToken);
        try
        {
            var batch = new List<KeyValuePair<(int DeckId, DateOnly Date), (int Views, int GuestDownloads)>>();
            foreach (var key in _pending.Keys.ToList())
            {
                if (_pending.TryRemove(key, out var counts))
                    batch.Add(new(key, counts));
            }

            if (batch.Count == 0) return;

            try
            {
                await WriteAsync(batch, cancellationToken);
            }
            catch (Exception ex)
            {
                foreach (var (key, counts) in batch)
                    _pending.AddOrUpdate(key, counts, (_, c) => (c.Views + counts.Views, c.GuestDownloads + counts.GuestDownloads));
                logger.LogWarning(ex, "Deck activity flush failed; {Count} buckets kept for retry", batch.Count);
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private async Task WriteAsync(List<KeyValuePair<(int DeckId, DateOnly Date), (int Views, int GuestDownloads)>> batch,
                                  CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var deckIds = batch.Select(b => b.Key.DeckId).Distinct().ToList();
        var knownDecks = await context.Decks.Where(d => deckIds.Contains(d.DeckId)).Select(d => d.DeckId).ToHashSetAsync(cancellationToken);
        var dates = batch.Select(b => b.Key.Date).Distinct().ToList();
        var existing = await context.DeckActivityDailies
                                    .Where(a => deckIds.Contains(a.DeckId) && dates.Contains(a.Date))
                                    .ToDictionaryAsync(a => (a.DeckId, a.Date), cancellationToken);

        foreach (var (key, counts) in batch)
        {
            if (!knownDecks.Contains(key.DeckId)) continue;
            if (existing.TryGetValue((key.DeckId, key.Date), out var row))
            {
                row.Views += counts.Views;
                row.GuestDownloads += counts.GuestDownloads;
            }
            else
            {
                context.DeckActivityDailies.Add(new DeckActivityDaily
                {
                    DeckId = key.DeckId, Date = key.Date, Views = counts.Views, GuestDownloads = counts.GuestDownloads
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}

public class DeckActivityFlushService(IDeckActivityBuffer buffer, ILogger<DeckActivityFlushService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await buffer.FlushAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        try
        {
            await buffer.FlushAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Final deck activity flush failed on shutdown");
        }
    }
}
