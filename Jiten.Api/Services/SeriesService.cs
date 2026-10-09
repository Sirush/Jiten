using System.Linq.Expressions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public sealed class SeriesService(JitenDbContext db, FranchiseService franchise)
{
    private static readonly Expression<Func<Series, SeriesRefDto>> RefProjection = s => new SeriesRefDto
    {
        SeriesId = s.SeriesId, Name = s.Name, Kind = s.Kind
    };

    public static IQueryable<Series> Filter(IQueryable<Series> query, string? text, SeriesKind? kind)
    {
        if (kind is { } k)
            query = query.Where(s => s.Kind == k);

        if (!string.IsNullOrWhiteSpace(text))
        {
            var needle = text.Trim().ToLower();
            query = query.Where(s => s.Name.ToLower().Contains(needle));
        }

        return query;
    }

    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Name cannot be empty.";
        if (name.Trim().Length > FranchiseNaming.MaxNameLength)
            return $"Name must be at most {FranchiseNaming.MaxNameLength} characters.";
        return null;
    }

    public Task<SeriesRefDto> ToRefAsync(int seriesId, CancellationToken ct = default) =>
        db.Series.AsNoTracking().Where(s => s.SeriesId == seriesId).Select(RefProjection).FirstAsync(ct);

    /// <summary>Sets <see cref="DeckDto.Series"/> on every deck.</summary>
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
                         .ToDictionary(g => g.Key, g => g.Select(r => r.Ref).OrderBy(r => r.Kind).ThenBy(r => r.Name).ToList());

        foreach (var deck in decks)
            deck.Series = byDeck.GetValueOrDefault(deck.DeckId) ?? [];
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
            Name = s.Name,
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
            Name = series.Name,
            Kind = series.Kind,
            FranchiseId = await SeriesHome.FranchiseAsync(db, seriesId, ct),
            Members = members
        };
    }
}
