using System.Linq.Expressions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public sealed class SeriesService(JitenDbContext db, FranchiseService franchise)
{
    private const int SettingDeckCap = 20;

    private static readonly Expression<Func<Series, SeriesRefDto>> RefProjection = s => new SeriesRefDto
    {
        SeriesId = s.SeriesId, OriginalTitle = s.OriginalTitle, RomajiTitle = s.RomajiTitle, EnglishTitle = s.EnglishTitle, Kind = s.Kind
    };

    public static IQueryable<Series> Filter(IQueryable<Series> query, string? text, SeriesKind? kind)
    {
        if (kind is { } k)
            query = query.Where(s => s.Kind == k);

        if (!string.IsNullOrWhiteSpace(text))
        {
            var needle = text.Trim().ToLower();
            query = query.Where(s => s.OriginalTitle.ToLower().Contains(needle) ||
                                     (s.RomajiTitle != null && s.RomajiTitle.ToLower().Contains(needle)) ||
                                     (s.EnglishTitle != null && s.EnglishTitle.ToLower().Contains(needle)));
        }

        return query;
    }

    public Task<SeriesRefDto> ToRefAsync(int seriesId, CancellationToken ct = default) =>
        db.Series.AsNoTracking().Where(s => s.SeriesId == seriesId).Select(RefProjection).FirstAsync(ct);

    /// <summary>Sets <see cref="DeckDto.Series"/> and <see cref="DeckDto.SettingDecks"/> on every deck.</summary>
    public async Task ApplyRefsAsync(IReadOnlyCollection<DeckDto> decks, CancellationToken ct = default)
    {
        if (decks.Count == 0)
            return;

        var ids = decks.Select(d => d.DeckId).Distinct().ToList();
        var rows = await db.SeriesMembers.AsNoTracking()
                           .Where(m => ids.Contains(m.DeckId))
                           .Join(db.Series.AsNoTracking().Select(RefProjection), m => m.SeriesId, r => r.SeriesId, (m, r) => new { m.DeckId, Ref = r })
                           .ToListAsync(ct);

        var byDeck = rows.GroupBy(r => r.DeckId)
                         .ToDictionary(g => g.Key, g => g.Select(r => r.Ref).OrderBy(r => r.Kind).ThenBy(r => r.OriginalTitle).ToList());

        foreach (var deck in decks)
            deck.Series = byDeck.GetValueOrDefault(deck.DeckId) ?? [];

        await ApplySettingDecksAsync(decks, ct);
    }

    private async Task ApplySettingDecksAsync(IReadOnlyCollection<DeckDto> decks, CancellationToken ct)
    {
        var settingIds = decks.SelectMany(d => d.Series).Where(s => s.Kind == SeriesKind.Setting).Select(s => s.SeriesId).Distinct().ToList();
        if (settingIds.Count == 0)
            return;

        var mates = await db.SeriesMembers.AsNoTracking()
                            .Where(m => settingIds.Contains(m.SeriesId))
                            .Select(m => new { m.SeriesId, m.DeckId })
                            .ToListAsync(ct);
        var mateIds = mates.Select(m => m.DeckId).Distinct().ToList();
        var mateDecks = await db.Decks.AsNoTracking()
                                .Include(d => d.DeckDifficulty)
                                .Where(d => mateIds.Contains(d.DeckId))
                                .ToDictionaryAsync(d => d.DeckId, ct);
        var matesBySetting = mates.GroupBy(m => m.SeriesId).ToDictionary(g => g.Key, g => g.Select(m => m.DeckId).ToList());

        foreach (var deck in decks)
        {
            // Same-franchise mates are already reachable from the franchise page.
            deck.SettingDecks = deck.Series.Where(s => s.Kind == SeriesKind.Setting)
                                    .SelectMany(s => matesBySetting.GetValueOrDefault(s.SeriesId) ?? [])
                                    .Distinct()
                                    .Where(id => id != deck.DeckId)
                                    .Select(id => mateDecks.GetValueOrDefault(id))
                                    .OfType<Deck>()
                                    .Where(d => deck.FranchiseId == null || d.FranchiseId != deck.FranchiseId)
                                    .OrderBy(d => FranchiseNaming.ReleaseOrder(d.ReleaseDate))
                                    .ThenBy(d => d.DeckId)
                                    .Take(SettingDeckCap)
                                    .Select(d => new DeckRefDto(d))
                                    .ToList();
        }
    }

    public async Task<List<SeriesSummaryDto>> BuildSummariesAsync(IReadOnlyList<Series> series, CancellationToken ct = default)
    {
        if (series.Count == 0)
            return [];

        var ids = series.Select(s => s.SeriesId).ToList();
        var deckCounts = await db.SeriesMembers.AsNoTracking()
                                 .Where(m => ids.Contains(m.SeriesId))
                                 .GroupBy(m => m.SeriesId)
                                 .Select(g => new { SeriesId = g.Key, Count = g.Count() })
                                 .ToDictionaryAsync(g => g.SeriesId, g => g.Count, ct);

        return series.Select(s => new SeriesSummaryDto
        {
            SeriesId = s.SeriesId,
            OriginalTitle = s.OriginalTitle,
            RomajiTitle = s.RomajiTitle,
            EnglishTitle = s.EnglishTitle,
            Kind = s.Kind,
            DeckCount = deckCounts.GetValueOrDefault(s.SeriesId)
        }).ToList();
    }

    public async Task<SeriesDetailDto?> BuildDetailAsync(int seriesId, CancellationToken ct = default)
    {
        var series = await db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.SeriesId == seriesId, ct);
        if (series == null)
            return null;

        var deckIds = await db.SeriesMembers.AsNoTracking()
                              .Where(m => m.SeriesId == seriesId)
                              .Select(m => m.DeckId)
                              .ToListAsync(ct);

        var members = (await franchise.LoadNodesAsync(deckIds, ct))
                      .OrderBy(m => FranchiseNaming.ReleaseOrder(DateOnly.FromDateTime(m.ReleaseDate)))
                      .ThenBy(m => m.DeckId)
                      .ToList();

        return new SeriesDetailDto
        {
            SeriesId = series.SeriesId,
            OriginalTitle = series.OriginalTitle,
            RomajiTitle = series.RomajiTitle,
            EnglishTitle = series.EnglishTitle,
            Kind = series.Kind,
            FranchiseId = await SeriesHome.FranchiseAsync(db, seriesId, ct),
            Members = members
        };
    }
}
