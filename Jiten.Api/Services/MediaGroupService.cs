using System.Security.Cryptography;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services;

/// <summary>A franchise, a series, or a line identified by its anchor deck.</summary>
public readonly record struct MediaGroupRef(MediaGroupKind Kind, int Id);

/// <summary>An existing group's titles (a line's are its anchor's) and every deck it holds, filters not applied.</summary>
public sealed record MediaGroupDescription(MediaGroupTitlesDto Titles, int? FranchiseId, IReadOnlyList<int> DeckIds);

public sealed record MediaGroupFilter(IReadOnlyCollection<MediaType>? MediaTypes, IReadOnlyCollection<int>? ExcludedDeckIds);

public sealed class MediaGroupService(JitenDbContext db, IMemoryCache memoryCache)
{
    private static readonly TimeSpan StatsTtl = TimeSpan.FromHours(1);

    /// <summary>Every deck of the group narrowed by the filters; null when the group does not exist.</summary>
    public async Task<List<int>?> ResolveDeckIdsAsync(MediaGroupRef group, IReadOnlyCollection<MediaType>? mediaTypes = null,
                                                      IReadOnlyCollection<int>? excludedDeckIds = null, CancellationToken ct = default)
    {
        if (await DescribeAsync(group, ct) is not { } description)
            return null;

        return (await NarrowAsync([(description.DeckIds, new MediaGroupFilter(mediaTypes, excludedDeckIds))], ct))[0];
    }

    /// <summary>Null when the group does not exist.</summary>
    public async Task<MediaGroupDescription?> DescribeAsync(MediaGroupRef group, CancellationToken ct = default) =>
        (await DescribeManyAsync([group], ct)).GetValueOrDefault(group);

    /// <summary>Descriptions of the groups that still exist, a few queries per kind however many groups there are.</summary>
    public async Task<Dictionary<MediaGroupRef, MediaGroupDescription>> DescribeManyAsync(IEnumerable<MediaGroupRef> groups,
                                                                                          CancellationToken ct = default)
    {
        var refs = groups.ToHashSet();
        var result = new Dictionary<MediaGroupRef, MediaGroupDescription>();
        List<int> IdsOf(MediaGroupKind kind) => refs.Where(g => g.Kind == kind).Select(g => g.Id).ToList();

        var franchiseIds = IdsOf(MediaGroupKind.Franchise);
        if (franchiseIds.Count > 0)
        {
            var franchises = await db.Franchises.AsNoTracking()
                                     .Where(f => franchiseIds.Contains(f.FranchiseId))
                                     .Select(f => new { f.FranchiseId, f.OriginalTitle, f.RomajiTitle, f.EnglishTitle })
                                     .ToListAsync(ct);
            var decks = (await db.Decks.AsNoTracking()
                                 .Where(d => d.FranchiseId != null && franchiseIds.Contains(d.FranchiseId.Value))
                                 .Select(d => new { d.DeckId, FranchiseId = d.FranchiseId!.Value })
                                 .ToListAsync(ct))
                        .ToLookup(d => d.FranchiseId, d => d.DeckId);

            foreach (var f in franchises)
                result[new MediaGroupRef(MediaGroupKind.Franchise, f.FranchiseId)] =
                    new MediaGroupDescription(Titles(f.OriginalTitle, f.RomajiTitle, f.EnglishTitle), f.FranchiseId, decks[f.FranchiseId].ToList());
        }

        var seriesIds = IdsOf(MediaGroupKind.Series);
        if (seriesIds.Count > 0)
        {
            var series = await db.Series.AsNoTracking()
                                 .Where(s => seriesIds.Contains(s.SeriesId))
                                 .Select(s => new { s.SeriesId, s.OriginalTitle, s.RomajiTitle, s.EnglishTitle })
                                 .ToListAsync(ct);
            var members = (await db.SeriesMembers.AsNoTracking()
                                   .Where(m => seriesIds.Contains(m.SeriesId))
                                   .Select(m => new { m.SeriesId, m.DeckId })
                                   .ToListAsync(ct))
                          .ToLookup(m => m.SeriesId, m => m.DeckId);
            var homes = await SeriesHome.FranchisesAsync(db, seriesIds, ct);

            foreach (var s in series)
                result[new MediaGroupRef(MediaGroupKind.Series, s.SeriesId)] =
                    new MediaGroupDescription(Titles(s.OriginalTitle, s.RomajiTitle, s.EnglishTitle),
                                              homes.TryGetValue(s.SeriesId, out var home) ? home : null, members[s.SeriesId].ToList());
        }

        var anchorIds = IdsOf(MediaGroupKind.Line);
        if (anchorIds.Count > 0)
        {
            var anchors = await db.Decks.AsNoTracking()
                                  .Where(d => anchorIds.Contains(d.DeckId))
                                  .Select(d => new { d.DeckId, d.OriginalTitle, d.RomajiTitle, d.EnglishTitle, d.FranchiseId })
                                  .ToListAsync(ct);
            var lines = await StoryLines.LinesOfAsync(db, anchors.Select(a => a.DeckId).ToList(), ct);

            foreach (var anchor in anchors)
                result[new MediaGroupRef(MediaGroupKind.Line, anchor.DeckId)] =
                    new MediaGroupDescription(Titles(anchor.OriginalTitle, anchor.RomajiTitle, anchor.EnglishTitle), anchor.FranchiseId,
                                              lines[anchor.DeckId]);
        }

        return result;
    }

    private static MediaGroupTitlesDto Titles(string originalTitle, string? romajiTitle, string? englishTitle) =>
        MediaGroupTitlesDto.From(GroupTitles.Of(originalTitle, romajiTitle, englishTitle));

    /// <summary>Each deck set narrowed by its filters, with one media type query for all of them.</summary>
    public async Task<List<int>[]> NarrowAsync(IReadOnlyList<(IReadOnlyList<int> DeckIds, MediaGroupFilter Filter)> sets, CancellationToken ct = default)
    {
        var typedIds = sets.Where(s => s.Filter.MediaTypes is { Count: > 0 }).SelectMany(s => s.DeckIds).Distinct().ToList();
        var mediaTypeOf = typedIds.Count == 0
            ? new Dictionary<int, MediaType>()
            : await db.Decks.AsNoTracking()
                      .Where(d => typedIds.Contains(d.DeckId))
                      .ToDictionaryAsync(d => d.DeckId, d => d.MediaType, ct);

        return sets.Select(s =>
        {
            var (excluded, mediaTypes) = (s.Filter.ExcludedDeckIds, s.Filter.MediaTypes);
            return s.DeckIds.Where(id => excluded is not { Count: > 0 } || !excluded.Contains(id))
                    .Where(id => mediaTypes is not { Count: > 0 } || (mediaTypeOf.TryGetValue(id, out var type) && mediaTypes.Contains(type)))
                    .ToList();
        }).ToArray();
    }

    /// <summary>Live stats of the deck set, cached by the hash of its sorted ids.</summary>
    public async Task<MediaGroupStatsDto> GetStatsAsync(IReadOnlyCollection<int> deckIds, CancellationToken ct = default)
    {
        var sorted = deckIds.Distinct().Order().ToArray();
        var bytes = new byte[sorted.Length * sizeof(int)];
        Buffer.BlockCopy(sorted, 0, bytes, 0, bytes.Length);
        var key = "media-group-stats:" + Convert.ToHexString(SHA256.HashData(bytes));

        if (memoryCache.TryGetValue(key, out MediaGroupStatsDto? cached) && cached != null)
            return cached;

        var stats = await MediaGroupStats.ComputeAsync(db, sorted, ct);
        memoryCache.Set(key, stats, StatsTtl);
        return stats;
    }
}
