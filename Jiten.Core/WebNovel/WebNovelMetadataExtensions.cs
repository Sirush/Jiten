using Jiten.Core.Data;
using Jiten.Core.Data.Providers;

namespace Jiten.Core.WebNovel;

public static class WebNovelMetadataExtensions
{
    /// <summary>
    /// Provider info as deck metadata. Shared by the import (which creates the deck) and the metadata
    /// refresh (which re-applies genre and tag mappings), so both see the same genres, keywords and links.
    /// </summary>
    public static Metadata ToMetadata(this WebNovelInfo info)
    {
        var metadata = new Metadata
        {
            OriginalTitle = info.Title,
            Description = info.Synopsis,
            ReleaseDate = info.FirstPublishedAt?.UtcDateTime,
            IsAdultOnly = info.IsAdultOnly,
            Links = [new Link { LinkType = LinkType.Syosetsu, Url = info.Url }],
            Tags = info.Keywords.Select(k => new MetadataTag { Name = k, Percentage = 100 }).ToList(),

            Genres = info.Keywords.ToList()
        };

        if (!string.IsNullOrEmpty(info.Genre))
        {
            metadata.Genres.Add(info.Genre);

            // Also offered to the tag mappings: a Narou genre carries information no Jiten genre holds
            // (異世界〔恋愛〕 is Romance *and* Isekai), and the two mapping tables are separate lookups.
            metadata.Tags.Add(new MetadataTag { Name = info.Genre, Percentage = 100 });
        }

        return metadata;
    }
}
