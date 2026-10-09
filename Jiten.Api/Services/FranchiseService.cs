using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public sealed class FranchiseService(JitenDbContext db, UserDbContext userContext, FranchiseResponseCache responseCache)
{
    private const int SettingOutsideCap = 20;

    /// <summary>Every viewer reads the shared cached build; a signed-in viewer gets a copy decorated with their coverage.</summary>
    public async Task<FranchiseDto?> GetForViewerAsync(int franchiseId, string? userId, CancellationToken ct = default)
    {
        var dto = await GetCachedAsync(franchiseId, ct);
        return dto == null ? null : await ForViewerAsync(dto, userId);
    }

    private Task<FranchiseDto?> GetCachedAsync(int franchiseId, CancellationToken ct) =>
        responseCache.GetOrBuildAsync(franchiseId, () => BuildAsync(franchiseId, ct));

    private async Task<FranchiseDto> ForViewerAsync(FranchiseDto dto, string? userId)
    {
        if (userId == null)
            return dto;

        var copy = dto.CloneNodes();
        await ApplyCoverageAsync(copy.Nodes.Concat(copy.Settings.SelectMany(s => s.Outside)).ToList(), userId);
        return copy;
    }

    public async Task ApplyCoverageAsync(IReadOnlyCollection<FranchiseNodeDto> nodes, string userId)
    {
        var coverages = await UserCoverageChunkHelper.GetCoverage(userContext, userId, nodes.Select(n => n.DeckId).Distinct().ToList());
        foreach (var node in nodes)
        {
            if (coverages.MatureCoverage.TryGetValue(node.DeckId, out var c)) node.Coverage = c;
            if (coverages.MatureUniqueCoverage.TryGetValue(node.DeckId, out var uc)) node.UniqueCoverage = uc;
        }
    }

    /// <summary>Null when the franchise does not exist.</summary>
    public async Task<FranchiseDto?> BuildAsync(int franchiseId, CancellationToken ct = default)
    {
        var row = await db.Franchises.AsNoTracking().FirstOrDefaultAsync(f => f.FranchiseId == franchiseId, ct);
        if (row == null)
            return null;

        var deckIds = await db.Decks.AsNoTracking()
                              .Where(d => d.FranchiseId == franchiseId)
                              .Select(d => d.DeckId)
                              .ToListAsync(ct);

        var dto = await BuildForMembersAsync(deckIds, ct);
        dto.FranchiseId = row.FranchiseId;
        dto.Name = row.Name;
        dto.NameIsManual = row.NameIsManual;
        return dto;
    }

    /// <summary>The deck's franchise, or the deck alone with a null franchise id; null when the deck does not exist.</summary>
    public async Task<FranchiseDto?> BuildForDeckAsync(int deckId, CancellationToken ct = default)
    {
        var deck = await db.Decks.AsNoTracking()
                           .Where(d => d.DeckId == deckId)
                           .Select(d => new { d.FranchiseId })
                           .FirstOrDefaultAsync(ct);
        if (deck == null)
            return null;

        if (deck.FranchiseId is { } franchiseId && await BuildAsync(franchiseId, ct) is { } franchise)
            return franchise;

        return await BuildForMembersAsync([deckId], ct);
    }

    /// <summary>The deck's franchise widened to the given decks, with their links and memberships; null when the deck does not exist.</summary>
    public async Task<FranchiseDto?> BuildForBoardAsync(int deckId, IReadOnlyCollection<int> boardDeckIds, CancellationToken ct = default)
    {
        var franchise = await BuildForDeckAsync(deckId, ct);
        if (franchise == null)
            return null;

        var deckIds = franchise.Nodes.Select(n => n.DeckId).ToHashSet();
        if (boardDeckIds.All(deckIds.Contains))
            return franchise;

        deckIds.UnionWith(boardDeckIds);
        var dto = await BuildForMembersAsync(deckIds.ToList(), ct);
        dto.FranchiseId = franchise.FranchiseId;
        dto.Name = franchise.Name;
        dto.NameIsManual = franchise.NameIsManual;
        return dto;
    }

    private async Task<FranchiseDto> BuildForMembersAsync(List<int> deckIds, CancellationToken ct)
    {
        var members = deckIds.ToHashSet();
        var edges = await StoryLines.LinksAmong(db.DeckRelationships.AsNoTracking(), deckIds)
                                    .Select(r => new FranchiseEdgeDto
                                    {
                                        SourceDeckId = r.SourceDeckId,
                                        TargetDeckId = r.TargetDeckId,
                                        RelationshipType = r.RelationshipType
                                    })
                                    .ToListAsync(ct);

        var memberships = await db.SeriesMembers.AsNoTracking()
                                  .Where(m => deckIds.Contains(m.DeckId) && m.Series.Kind == SeriesKind.Series)
                                  .Select(m => new { m.SeriesId, m.DeckId })
                                  .ToListAsync(ct);

        var nodes = await LoadNodesAsync(deckIds, ct);
        var lines = BuildLines(nodes, edges);
        var dto = new FranchiseDto
        {
            Nodes = nodes,
            Edges = edges,
            Lines = lines,
            Settings = await BuildSettingsAsync(members, ct)
        };

        if (memberships.Count == 0)
            return dto;

        var seriesIds = memberships.Select(m => m.SeriesId).Distinct().ToList();
        var series = await db.Series.AsNoTracking()
                             .Where(s => seriesIds.Contains(s.SeriesId))
                             .Select(s => new { s.SeriesId, s.Name })
                             .ToListAsync(ct);

        dto.Series = series.OrderBy(s => s.Name).ThenBy(s => s.SeriesId)
                           .Select(s => new FranchiseSeriesDto
                           {
                               SeriesId = s.SeriesId,
                               Name = s.Name,
                               MemberDeckIds = memberships.Where(m => m.SeriesId == s.SeriesId).Select(m => m.DeckId).Order().ToList()
                           })
                           .ToList();

        var lineOf = lines.SelectMany(l => l.DeckIds.Select(id => (DeckId: id, l.AnchorDeckId))).ToDictionary(x => x.DeckId, x => x.AnchorDeckId);
        dto.PreferredView = PrefersSeriesView(dto.Series, lineOf) ? FranchiseViews.Series : FranchiseViews.Timeline;

        return dto;
    }

    private static List<FranchiseLineDto> BuildLines(List<FranchiseNodeDto> nodes, List<FranchiseEdgeDto> edges)
    {
        var byId = nodes.ToDictionary(n => n.DeckId);
        return StoryLines.Components(byId.Keys, edges.Select(e => (e.SourceDeckId, e.TargetDeckId)))
                         .Select(component =>
                         {
                             var anchor = component.Select(id => byId[id])
                                                   .OrderBy(n => FranchiseNaming.ReleaseOrder(DateOnly.FromDateTime(n.ReleaseDate)))
                                                   .ThenBy(n => n.DeckId)
                                                   .First();
                             return (Anchor: anchor, Line: new FranchiseLineDto { AnchorDeckId = anchor.DeckId, DeckIds = component.Order().ToList() });
                         })
                         .OrderBy(l => FranchiseNaming.ReleaseOrder(DateOnly.FromDateTime(l.Anchor.ReleaseDate)))
                         .ThenBy(l => l.Anchor.DeckId)
                         .Select(l => l.Line)
                         .ToList();
    }

    /// <summary>An entry is a line holding at least one member; one series with two entries is enough.</summary>
    private static bool PrefersSeriesView(List<FranchiseSeriesDto> series, Dictionary<int, int> lineOf) =>
        series.Any(s => s.MemberDeckIds.Select(id => lineOf.GetValueOrDefault(id, id)).Distinct().Count() >= 2);

    private async Task<List<FranchiseSettingDto>> BuildSettingsAsync(HashSet<int> franchiseIds, CancellationToken ct)
    {
        var ids = franchiseIds.ToList();
        var settings = await db.Series.AsNoTracking()
                               .Where(s => s.Kind == SeriesKind.Setting && s.Members.Any(m => ids.Contains(m.DeckId)))
                               .Select(s => new { s.SeriesId, s.Name })
                               .ToListAsync(ct);
        if (settings.Count == 0)
            return [];

        var settingIds = settings.Select(s => s.SeriesId).ToList();
        var members = await db.SeriesMembers.AsNoTracking()
                              .Where(m => settingIds.Contains(m.SeriesId))
                              .Select(m => new { m.SeriesId, m.DeckId, m.Deck.ReleaseDate })
                              .ToListAsync(ct);

        var outsideBySetting = members.Where(m => !franchiseIds.Contains(m.DeckId))
                                      .GroupBy(m => m.SeriesId)
                                      .ToDictionary(g => g.Key, g => g.OrderBy(m => FranchiseNaming.ReleaseOrder(m.ReleaseDate))
                                                                      .ThenBy(m => m.DeckId)
                                                                      .Select(m => m.DeckId)
                                                                      .ToList());

        var shownOutside = outsideBySetting.Values.SelectMany(v => v.Take(SettingOutsideCap)).Distinct().ToList();
        var outsideNodes = (await LoadNodesAsync(shownOutside, ct)).ToDictionary(n => n.DeckId);

        return settings.OrderBy(s => s.Name).ThenBy(s => s.SeriesId)
                       .Select(s =>
                       {
                           var outside = outsideBySetting.GetValueOrDefault(s.SeriesId) ?? [];
                           return new FranchiseSettingDto
                           {
                               SeriesId = s.SeriesId,
                               Name = s.Name,
                               MemberDeckIds = members.Where(m => m.SeriesId == s.SeriesId && franchiseIds.Contains(m.DeckId))
                                                      .Select(m => m.DeckId).Order().ToList(),
                               Outside = outside.Take(SettingOutsideCap)
                                                .Where(outsideNodes.ContainsKey)
                                                .Select(id => outsideNodes[id])
                                                .ToList(),
                               OutsideCount = outside.Count
                           };
                       })
                       .ToList();
    }

    public async Task<List<FranchiseNodeDto>> LoadNodesAsync(IReadOnlyCollection<int> deckIds, CancellationToken ct = default)
    {
        if (deckIds.Count == 0)
            return [];

        var ids = deckIds.ToList();
        var decks = await db.Decks.AsNoTracking()
                            .Where(d => ids.Contains(d.DeckId))
                            .Select(d => new Deck
                            {
                                DeckId = d.DeckId, OriginalTitle = d.OriginalTitle, RomajiTitle = d.RomajiTitle, EnglishTitle = d.EnglishTitle,
                                CoverName = d.CoverName, MediaType = d.MediaType, ReleaseDate = d.ReleaseDate,
                                Difficulty = d.Difficulty, DifficultyOverride = d.DifficultyOverride, DeckDifficulty = d.DeckDifficulty
                            })
                            .ToListAsync(ct);

        return decks.Select(d => new FranchiseNodeDto
        {
            DeckId = d.DeckId,
            OriginalTitle = d.OriginalTitle,
            RomajiTitle = d.RomajiTitle ?? "",
            EnglishTitle = d.EnglishTitle ?? "",
            CoverName = d.CoverName,
            MediaType = d.MediaType,
            ReleaseDate = d.ReleaseDate.ToDateTime(new TimeOnly()),
            Difficulty = DifficultyMapper.MapDeck(d),
            DifficultyRaw = DifficultyMapper.GetAdjustedDifficulty(d)
        }).ToList();
    }
}
