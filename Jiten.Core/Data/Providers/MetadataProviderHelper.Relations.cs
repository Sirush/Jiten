using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core;

public static partial class MetadataProviderHelper
{
    public static async Task ProcessRelations(JitenDbContext context, int deckId, List<MetadataRelation> relations)
    {
        if (relations.Count == 0)
            return;

        foreach (var relation in relations)
        {
            await ProcessSingleRelation(context, deckId, relation);
        }

        await context.SaveChangesAsync();
    }

    private static async Task ProcessSingleRelation(JitenDbContext context, int deckId, MetadataRelation relation)
    {
        var targetDeckIds = await FindDecksByLinkId(context, relation.LinkType, relation.ExternalId, relation.TargetMediaType);

        foreach (var targetDeckId in targetDeckIds)
        {
            if (targetDeckId == deckId)
                continue;

            var sourceDeckId = relation.SwapDirection ? targetDeckId : deckId;
            var actualTargetId = relation.SwapDirection ? deckId : targetDeckId;

            var exists = await context.DeckRelationships.AnyAsync(r =>
                r.SourceDeckId == sourceDeckId &&
                r.TargetDeckId == actualTargetId &&
                r.RelationshipType == relation.RelationshipType);

            if (exists)
                continue;

            var localExists = context.DeckRelationships.Local.Any(r =>
                r.SourceDeckId == sourceDeckId &&
                r.TargetDeckId == actualTargetId &&
                r.RelationshipType == relation.RelationshipType);

            if (localExists)
                continue;

            var inverseType = DeckRelationship.GetInverse(relation.RelationshipType);
            var inverseExists = await context.DeckRelationships.AnyAsync(r =>
                r.SourceDeckId == actualTargetId &&
                r.TargetDeckId == sourceDeckId &&
                r.RelationshipType == inverseType);

            if (inverseExists)
                continue;

            var localInverseExists = context.DeckRelationships.Local.Any(r =>
                r.SourceDeckId == actualTargetId &&
                r.TargetDeckId == sourceDeckId &&
                r.RelationshipType == inverseType);

            if (localInverseExists)
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
        var query = context.Decks
            .Include(d => d.Links)
            .Where(d => d.Links.Any(l => l.LinkType == linkType));

        if (mediaType.HasValue)
            query = query.Where(d => d.MediaType == mediaType.Value);

        var links = await query
            .SelectMany(d => d.Links.Where(l => l.LinkType == linkType)
                .Select(l => new { d.DeckId, l.Url }))
            .ToListAsync();

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
