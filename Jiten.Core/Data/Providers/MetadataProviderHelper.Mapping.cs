using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Core.Data.Providers;
using Jiten.Core.Services;
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

    /// <summary>The one write path for a provider refetch onto an existing deck: fills, merges and maps without overriding a person's locks or removals.</summary>
    public static async Task ApplyProviderMetadataAsync(JitenDbContext context, Deck deck, Metadata metadata, LinkType linkType,
                                                        DeckMetadataExclusionSet exclusions)
    {
        ApplyScalars(deck, metadata);
        MergeAliases(deck, metadata.Aliases, exclusions);
        MergeDictionaryEntries(deck, metadata.DictionaryEntries, exclusions);
        await ApplyGenreAndTagMappings(context, deck, metadata, linkType, exclusions);
    }

    /// <summary>Fills empty release date and description only, and never a field a person locked.</summary>
    internal static void ApplyScalars(Deck deck, Metadata metadata)
    {
        if (deck.ReleaseDate == default && metadata.ReleaseDate != null && !deck.MetadataLocks.HasFlag(MetadataField.ReleaseDate))
            deck.ReleaseDate = DateOnly.FromDateTime(metadata.ReleaseDate.Value);

        if (string.IsNullOrEmpty(deck.Description) && !deck.MetadataLocks.HasFlag(MetadataField.Description))
            deck.Description = metadata.Description?.Length > 2000 ? metadata.Description[..2000] : metadata.Description;
    }

    internal static void MergeAliases(Deck deck, IEnumerable<string> aliases, DeckMetadataExclusionSet exclusions)
    {
        foreach (var alias in aliases)
        {
            if (exclusions.ExcludesAlias(alias))
                continue;

            if (deck.Titles.All(t => !string.Equals(t.Title, alias, StringComparison.OrdinalIgnoreCase)))
                deck.Titles.Add(new DeckTitle { DeckId = deck.DeckId, Title = alias, TitleType = DeckTitleType.Alias });
        }
    }

    internal static void MergeDictionaryEntries(Deck deck, List<DeckDictionaryEntry> entries, DeckMetadataExclusionSet exclusions)
    {
        foreach (var entry in entries)
        {
            if (exclusions.ExcludesCharacterName(entry.Surface))
                continue;

            if (deck.DictionaryEntries.All(e => e.Surface != entry.Surface))
            {
                entry.DeckId = deck.DeckId;
                deck.DictionaryEntries.Add(entry);
            }
        }
    }

    /// <summary>Adds the provider's genres and tags and raises lower tag percentages; never removes, never re-adds an excluded value, never touches a manual tag.</summary>
    public static async Task ApplyGenreAndTagMappings(JitenDbContext context, Deck deck, Metadata metadata, LinkType linkType,
                                                      DeckMetadataExclusionSet exclusions)
    {
        var mapped = await MapGenresAndTags(context, metadata, linkType);

        foreach (var genre in mapped.Genres)
        {
            if (!exclusions.ExcludesGenre(genre) && deck.DeckGenres.All(dg => dg.Genre != genre))
                deck.DeckGenres.Add(new DeckGenre { DeckId = deck.DeckId, Genre = genre });
        }

        foreach (var (tagId, percentage) in mapped.Tags)
        {
            if (exclusions.ExcludesTag(tagId))
                continue;

            var existingDeckTag = deck.DeckTags.FirstOrDefault(dt => dt.TagId == tagId);

            if (existingDeckTag != null)
            {
                if (!existingDeckTag.IsManual && percentage > existingDeckTag.Percentage)
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
