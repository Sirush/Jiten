using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core;

public static partial class MetadataProviderHelper
{
    public sealed record MappedGenresAndTags(HashSet<Genre> Genres, Dictionary<int, byte> Tags);

    /// <summary>Translates a provider's genre and tag names into Jiten's; duplicate tag mappings keep the highest percentage.</summary>
    public static async Task<MappedGenresAndTags> MapGenresAndTags(JitenDbContext context, Metadata metadata, LinkType linkType)
    {
        var genres = new HashSet<Genre>();
        var tags = new Dictionary<int, byte>();

        if (metadata.Genres.Count > 0)
        {
            var genreMappings = await context.ExternalGenreMappings
                                             .AsNoTracking()
                                             .Where(m => m.Provider == linkType)
                                             .ToListAsync();

            var genreMappingsDict = new Dictionary<string, ExternalGenreMapping>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in genreMappings)
                genreMappingsDict.TryAdd(mapping.ExternalGenreName, mapping);

            foreach (var externalGenreName in metadata.Genres)
            {
                if (genreMappingsDict.TryGetValue(externalGenreName, out var mapping))
                    genres.Add(mapping.JitenGenre);
            }
        }

        if (metadata.IsAdultOnly)
            genres.Add(Genre.AdultOnly);

        const int NotOriginallyJpTagId = 249;
        if (metadata.IsNotOriginallyJapanese)
            tags[NotOriginallyJpTagId] = 100;

        if (metadata.Tags.Count > 0)
        {
            var tagMappings = await context.ExternalTagMappings
                                           .AsNoTracking()
                                           .Where(m => m.Provider == linkType)
                                           .ToListAsync();

            var tagMappingsDict = new Dictionary<string, ExternalTagMapping>(StringComparer.OrdinalIgnoreCase);
            foreach (var mapping in tagMappings)
                tagMappingsDict.TryAdd(mapping.ExternalTagName, mapping);

            foreach (var tag in metadata.Tags)
            {
                if (!tagMappingsDict.TryGetValue(tag.Name, out var mapping))
                    continue;

                var percentage = (byte)Math.Clamp(tag.Percentage, 0, 100);
                tags[mapping.TagId] = tags.TryGetValue(mapping.TagId, out var existing) ? Math.Max(existing, percentage) : percentage;
            }
        }

        return new MappedGenresAndTags(genres, tags);
    }

    /// <summary>
    /// Applies genre and tag mappings from external provider to Jiten's internal categories.
    /// Only adds NEW genres/tags - never removes existing ones.
    /// </summary>
    public static async Task ApplyGenreAndTagMappings(JitenDbContext context, Deck deck, Metadata metadata, LinkType linkType)
    {
        var mapped = await MapGenresAndTags(context, metadata, linkType);

        foreach (var genre in mapped.Genres)
        {
            if (deck.DeckGenres.All(dg => dg.Genre != genre))
                deck.DeckGenres.Add(new DeckGenre { DeckId = deck.DeckId, Genre = genre });
        }

        foreach (var (tagId, percentage) in mapped.Tags)
        {
            var existingDeckTag = deck.DeckTags.FirstOrDefault(dt => dt.TagId == tagId);

            if (existingDeckTag != null)
            {
                if (percentage > existingDeckTag.Percentage)
                    existingDeckTag.Percentage = percentage;
            }
            else
            {
                // Setting the Tag navigation property makes EF attempt an INSERT of the existing Tag
                deck.DeckTags.Add(new DeckTag { DeckId = deck.DeckId, TagId = tagId, Percentage = percentage });
            }
        }
    }
}
