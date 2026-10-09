using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jiten.Core;

public static partial class MetadataProviderHelper
{
    /// <summary>Stores relation edges, turning provider series / setting relations into group membership.</summary>
    public static async Task ProcessRelations(JitenDbContext context, int deckId, List<MetadataRelation> relations, ILogger? logger = null)
    {
        if (relations.Count == 0 || deckId <= 0)
            return;

        foreach (var relation in relations)
        {
            var groupKind = GroupKindOf(relation.RelationshipType);
            if (groupKind != null)
                await ProcessGroupRelation(context, deckId, relation, groupKind.Value, logger);
            else
                await ProcessSingleRelation(context, deckId, relation);
        }

        await context.SaveChangesAsync();
    }

    /// <summary>Providers still report series / setting links as relationship types 7 / 8; they are stored as membership only.</summary>
    public static SeriesKind? GroupKindOf(DeckRelationshipType type) => type switch
    {
        DeckRelationshipType.SameSeries => SeriesKind.Series,
        DeckRelationshipType.SameSetting => SeriesKind.Setting,
        _ => null
    };

    private static async Task ProcessGroupRelation(JitenDbContext context, int deckId, MetadataRelation relation, SeriesKind kind,
                                                   ILogger? logger)
    {
        var targetDeckIds = await FindDecksByLinkId(context, relation.LinkType, relation.ExternalId, relation.TargetMediaType);

        foreach (var targetDeckId in targetDeckIds.Distinct())
        {
            if (targetDeckId == deckId)
                continue;

            await AddPairToGroup(context, deckId, targetDeckId, kind, logger);
        }
    }

    /// <summary>Decks in different groups, or a deck in several, are skipped: picking one would silently merge or misfile.</summary>
    public static async Task AddPairToGroup(JitenDbContext context, int deckA, int deckB, SeriesKind kind, ILogger? logger = null)
    {
        var groupsA = await GroupIdsOf(context, deckA, kind);
        var groupsB = await GroupIdsOf(context, deckB, kind);

        if (groupsA.Intersect(groupsB).Any())
            return;

        if (groupsA.Count == 0 && groupsB.Count == 0)
        {
            var decks = await context.Decks.AsNoTracking()
                                     .Where(d => d.DeckId == deckA || d.DeckId == deckB)
                                     .Select(d => new { d.DeckId, d.OriginalTitle, d.ReleaseDate })
                                     .ToListAsync();
            if (decks.Count < 2)
                return;

            var earliest = decks.OrderBy(d => FranchiseNaming.ReleaseOrder(d.ReleaseDate))
                                .ThenBy(d => d.DeckId)
                                .First();
            var name = earliest.OriginalTitle.Length > FranchiseNaming.MaxNameLength
                ? earliest.OriginalTitle[..FranchiseNaming.MaxNameLength]
                : earliest.OriginalTitle;

            var series = new Series { Name = name, Kind = kind };
            series.Members.Add(new SeriesMember { DeckId = deckA });
            series.Members.Add(new SeriesMember { DeckId = deckB });
            context.Series.Add(series);
            await context.SaveChangesAsync();
            return;
        }

        if (groupsA.Count > 0 && groupsB.Count > 0)
        {
            logger?.LogWarning("Skipped {Kind} link between decks {DeckA} and {DeckB}: they sit in different groups ({GroupsA} / {GroupsB})",
                               kind, deckA, deckB, string.Join(",", groupsA), string.Join(",", groupsB));
            return;
        }

        var (grouped, loose, groups) = groupsA.Count > 0 ? (deckA, deckB, groupsA) : (deckB, deckA, groupsB);
        if (groups.Count > 1)
        {
            logger?.LogWarning("Skipped {Kind} link between decks {Grouped} and {Loose}: deck {Grouped} sits in several groups ({Groups})",
                               kind, grouped, loose, grouped, string.Join(",", groups));
            return;
        }

        context.SeriesMembers.Add(new SeriesMember { SeriesId = groups[0], DeckId = loose });
        await context.SaveChangesAsync();
    }

    private static Task<List<int>> GroupIdsOf(JitenDbContext context, int deckId, SeriesKind kind) =>
        context.SeriesMembers.AsNoTracking()
               .Where(m => m.DeckId == deckId && m.Series.Kind == kind)
               .Select(m => m.SeriesId)
               .ToListAsync();

    private static async Task ProcessSingleRelation(JitenDbContext context, int deckId, MetadataRelation relation)
    {
        var targetDeckIds = await FindDecksByLinkId(context, relation.LinkType, relation.ExternalId, relation.TargetMediaType);

        foreach (var targetDeckId in targetDeckIds)
        {
            var sourceDeckId = relation.SwapDirection ? targetDeckId : deckId;
            var actualTargetId = relation.SwapDirection ? deckId : targetDeckId;

            if (DeckRelationship.ValidateEdge(sourceDeckId, actualTargetId, relation.RelationshipType) != null)
                continue;

            var sameEdge = DeckRelationship.SameEdge(sourceDeckId, actualTargetId, relation.RelationshipType);
            if (context.DeckRelationships.Local.AsQueryable().Any(sameEdge) || await context.DeckRelationships.AnyAsync(sameEdge))
                continue;

            context.DeckRelationships.Add(new DeckRelationship
            {
                SourceDeckId = sourceDeckId,
                TargetDeckId = actualTargetId,
                RelationshipType = relation.RelationshipType
            });
        }
    }

    private static async Task<List<int>> FindDecksByLinkId(JitenDbContext context, LinkType linkType, string externalId, MediaType? mediaType)
    {
        var query = context.Set<Link>().AsNoTracking().Where(l => l.LinkType == linkType);

        if (mediaType.HasValue)
            query = query.Where(l => l.Deck.MediaType == mediaType.Value);

        var links = await query.Select(l => new { l.DeckId, l.Url }).ToListAsync();

        var result = new List<int>();

        foreach (var link in links)
        {
            var url = link.Url.TrimEnd('/');
            var lastSlashIndex = url.LastIndexOf('/');
            if (lastSlashIndex == -1)
                continue;

            var urlId = url.Substring(lastSlashIndex + 1);
            if (urlId.Equals(externalId, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(link.DeckId);
            }
        }

        return result;
    }
}
