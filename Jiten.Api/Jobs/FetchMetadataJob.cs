using Hangfire;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

public class FetchMetadataJob(IDbContextFactory<JitenDbContext> contextFactory, IConfiguration configuration)
{
    private const float ANILIST_DELAY = 2.2f;
    private const float GOOGLE_BOOKS_DELAY = 3f;
    private const float VNDB_DELAY = 3.5f;
    private const float TMDB_DELAY = 1.5f;
    private const float IGDB_DELAY = 1f;
    private const float JIKAN_DELAY = 1.5f;

    [Queue("anilist")]
    public async Task FetchAnilistMissingMetadata(int deckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        try
        {
            var deck = context.Decks
                .Include(d => d.Links)
                .Include(deck => deck.Titles)
                .Include(d => d.DeckGenres)
                .Include(d => d.DeckTags)
                .Include(d => d.DictionaryEntries)
                .First(d => d.DeckId == deckId);
            var link = deck.Links.FirstOrDefault(l => l.LinkType == LinkType.Anilist);

            if (link == null)
                throw new Exception($"No Anilist link found for deck with ID {deckId}.");

            var url = link.Url.TrimEnd('/');
            var lastSlashIndex = url.LastIndexOf('/');
            var id = int.Parse(url.Substring(lastSlashIndex + 1));

            var metadata = await MetadataProviderHelper.AnilistApi(id);

            if (metadata == null)
                throw new Exception($"Metadata for Anilist ID {id} not found.");

            if (deck.ReleaseDate == default && metadata.ReleaseDate != null)
                deck.ReleaseDate = DateOnly.FromDateTime(metadata.ReleaseDate.Value);

            if (string.IsNullOrEmpty(deck.Description))
                deck.Description = metadata.Description?.Length > 2000 ? metadata.Description?[..2000] : metadata.Description;

            if (metadata.Rating != null)
                deck.ExternalRating = (byte)metadata.Rating;

            foreach (var alias in metadata.Aliases)
            {
                if (deck.Titles.All(t => !string.Equals(t.Title, alias, StringComparison.OrdinalIgnoreCase)))
                    deck.Titles.Add(new DeckTitle(){DeckId = deck.DeckId, Title = alias, TitleType = DeckTitleType.Alias});
            }

            MergeDictionaryEntries(deck, metadata.DictionaryEntries);

            await MetadataProviderHelper.ApplyGenreAndTagMappings(context, deck, metadata, LinkType.Anilist);

            // Process relations from metadata
            if (metadata.Relations.Count > 0)
            {
                await MetadataProviderHelper.ProcessRelations(context, deckId, metadata.Relations);
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            await Task.Delay(TimeSpan.FromSeconds(ANILIST_DELAY));
        }
    }

    [Queue("books")]
    public async Task FetchGoogleBooksMissingMetadata(int deckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        try
        {
            var deck = context.Decks
                .Include(d => d.Links)
                .Include(deck => deck.Titles)
                .Include(d => d.DeckGenres)
                .Include(d => d.DeckTags)
                .First(d => d.DeckId == deckId);
            var link = deck.Links.FirstOrDefault(l => l.LinkType == LinkType.GoogleBooks);

            if (link == null)
                throw new Exception($"No Google Books link found for deck with ID {deckId}.");

            var url = link.Url.TrimEnd('/');
            var lastSlashIndex = url.LastIndexOf('/');
            var id = url.Substring(lastSlashIndex + 1);

            var metadata = await MetadataProviderHelper.GoogleBooksApi(id);

            if (metadata == null)
                throw new Exception($"Metadata for Google Books ID {id} not found.");

            if (deck.ReleaseDate == default && metadata.ReleaseDate != null)
                deck.ReleaseDate = DateOnly.FromDateTime(metadata.ReleaseDate.Value);

            if (string.IsNullOrEmpty(deck.Description))
                deck.Description = metadata.Description?.Length > 2000 ? metadata.Description?[..2000] : metadata.Description;

            if (metadata.Rating != null)
                deck.ExternalRating = (byte)metadata.Rating;

            foreach (var alias in metadata.Aliases)
            {
                if (deck.Titles.All(t => !string.Equals(t.Title, alias, StringComparison.OrdinalIgnoreCase)))
                    deck.Titles.Add(new DeckTitle(){DeckId = deck.DeckId, Title = alias, TitleType = DeckTitleType.Alias});
            }

            await MetadataProviderHelper.ApplyGenreAndTagMappings(context, deck, metadata, LinkType.GoogleBooks);

            await context.SaveChangesAsync();
        }
        finally
        {
            await Task.Delay(TimeSpan.FromSeconds(GOOGLE_BOOKS_DELAY));
        }
    }

    [Queue("vndb")]
    public async Task FetchVndbMissingMetadata(int deckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        try
        {
            var deck = context.Decks
                .Include(d => d.Links)
                .Include(deck => deck.Titles)
                .Include(d => d.DeckGenres)
                .Include(d => d.DeckTags)
                .Include(d => d.DictionaryEntries)
                .First(d => d.DeckId == deckId);
            var link = deck.Links.FirstOrDefault(l => l.LinkType == LinkType.Vndb);

            if (link == null)
                throw new Exception($"No VNDB link found for deck with ID {deckId}.");

            var url = link.Url.TrimEnd('/');
            var lastSlashIndex = url.LastIndexOf('/');
            var id = url.Substring(lastSlashIndex + 1);

            var metadata = await MetadataProviderHelper.VndbApi(id);

            if (metadata == null)
                throw new Exception($"Metadata for VNDB ID {id} not found.");

            if (deck.ReleaseDate == default && metadata.ReleaseDate != null)
                deck.ReleaseDate = DateOnly.FromDateTime(metadata.ReleaseDate.Value);

            if (string.IsNullOrEmpty(deck.Description))
                deck.Description = metadata.Description?.Length > 2000 ? metadata.Description?[..2000] : metadata.Description;

            if (metadata.Rating != null)
                deck.ExternalRating = (byte)metadata.Rating;

            foreach (var alias in metadata.Aliases)
            {
                if (deck.Titles.All(t => !string.Equals(t.Title, alias, StringComparison.OrdinalIgnoreCase)))
                    deck.Titles.Add(new DeckTitle(){DeckId = deck.DeckId, Title = alias, TitleType = DeckTitleType.Alias});
            }

            MergeDictionaryEntries(deck, metadata.DictionaryEntries);

            await MetadataProviderHelper.ApplyGenreAndTagMappings(context, deck, metadata, LinkType.Vndb);

            // Process relations from metadata
            if (metadata.Relations.Count > 0)
            {
                await MetadataProviderHelper.ProcessRelations(context, deckId, metadata.Relations);
            }

            // VNDB anime adaptations (from the database dump, matched to anime decks by MAL id)
            var animeRelations = MetadataProviderHelper.GetVndbAnimeRelations(id);
            if (animeRelations.Count > 0)
            {
                await MetadataProviderHelper.ProcessRelations(context, deckId, animeRelations);
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            await Task.Delay(TimeSpan.FromSeconds(VNDB_DELAY));
        }
    }

    [Queue("tmdb")]
    public async Task FetchTmdbMissingMetadata(int deckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        try
        {
            var deck = context.Decks
                .Include(d => d.Links)
                .Include(d => d.Titles)
                .Include(d => d.DeckGenres)
                .Include(d => d.DeckTags)
                .First(d => d.DeckId == deckId);
            var link = deck.Links.FirstOrDefault(l => l.LinkType == LinkType.Tmdb);

            if (link == null)
                throw new Exception($"No TMDB link found for deck with ID {deckId}.");

            var url = link.Url.TrimEnd('/');
            var lastSlashIndex = url.LastIndexOf('/');
            var id = url.Substring(lastSlashIndex + 1);

            string apiKey = configuration["TmdbApiKey"]!;

            Metadata metadata;
            if (deck.MediaType == MediaType.Movie)
                metadata = await MetadataProviderHelper.TmdbMovieApi(id, apiKey);
            else
                metadata = await MetadataProviderHelper.TmdbTvApi(id, apiKey);

            if (metadata == null)
                throw new Exception($"Metadata for TMDB ID {id} not found.");

            if (deck.ReleaseDate == default && metadata.ReleaseDate != null)
                deck.ReleaseDate = DateOnly.FromDateTime(metadata.ReleaseDate.Value);

            if (string.IsNullOrEmpty(deck.Description))
                deck.Description = metadata.Description?.Length > 2000 ? metadata.Description?[..2000] : metadata.Description;

            if (metadata.Rating != null)
                deck.ExternalRating = (byte)metadata.Rating;

            foreach (var alias in metadata.Aliases)
            {
                if (deck.Titles.All(t => !string.Equals(t.Title, alias, StringComparison.OrdinalIgnoreCase)))
                    deck.Titles.Add(new DeckTitle(){DeckId = deck.DeckId, Title = alias, TitleType = DeckTitleType.Alias});
            }

            await MetadataProviderHelper.ApplyGenreAndTagMappings(context, deck, metadata, LinkType.Tmdb);

            await context.SaveChangesAsync();
        }
        finally
        {
            await Task.Delay(TimeSpan.FromSeconds(TMDB_DELAY));
        }
    }
    
    [Queue("igdb")]
    public async Task FetchIgdbMissingMetadata(int deckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        try
        {
            var deck = context.Decks
                .Include(d => d.Links)
                .Include(d => d.Titles)
                .Include(d => d.DeckGenres)
                .Include(d => d.DeckTags)
                .First(d => d.DeckId == deckId);
            var link = deck.Links.FirstOrDefault(l => l.LinkType == LinkType.Igdb);

            if (link == null)
                throw new Exception($"No IGDB link found for deck with ID {deckId}.");

            var url = link.Url.TrimEnd('/');

            Metadata? metadata = await MetadataProviderHelper.IgdbApi(url,configuration["IgdbClientId"]!, configuration["IgdbClientSecret"]!);
            if (metadata == null)
                throw new Exception($"Metadata for IGDB URL {url} not found.");

            if (deck.ReleaseDate == default && metadata.ReleaseDate != null)
                deck.ReleaseDate = DateOnly.FromDateTime(metadata.ReleaseDate.Value);

            if (string.IsNullOrEmpty(deck.Description))
                deck.Description = metadata.Description?.Length > 2000 ? metadata.Description?[..2000] : metadata.Description;

            if (metadata.Rating != null)
                deck.ExternalRating = (byte)metadata.Rating;

            foreach (var alias in metadata.Aliases)
            {
                if (deck.Titles.All(t => !string.Equals(t.Title, alias, StringComparison.OrdinalIgnoreCase)))
                    deck.Titles.Add(new DeckTitle(){DeckId = deck.DeckId, Title = alias, TitleType = DeckTitleType.Alias});
            }

            await MetadataProviderHelper.ApplyGenreAndTagMappings(context, deck, metadata, LinkType.Igdb);

            await context.SaveChangesAsync();
        }
        finally
        {
            await Task.Delay(TimeSpan.FromSeconds(IGDB_DELAY));
        }
    }

    [Queue("jikan")]
    public async Task FetchJikanMissingMetadata(int deckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        try
        {
            var deck = context.Decks
                .Include(d => d.Links)
                .Include(d => d.Titles)
                .Include(d => d.DeckGenres)
                .Include(d => d.DeckTags)
                .Include(d => d.DictionaryEntries)
                .First(d => d.DeckId == deckId);
            var link = deck.Links.FirstOrDefault(l => l.LinkType == LinkType.Mal);

            if (link == null)
                throw new Exception($"No MAL link found for deck with ID {deckId}.");

            var url = link.Url.TrimEnd('/');
            var lastSlashIndex = url.LastIndexOf('/');
            var malId = int.Parse(url.Substring(lastSlashIndex + 1));

            var isAnime = url.Contains("/anime/");
            var metadata = isAnime
                ? await MetadataProviderHelper.JikanAnimeApi(malId)
                : await MetadataProviderHelper.JikanMangaApi(malId);

            if (metadata == null)
                throw new Exception($"Metadata for MAL ID {malId} not found.");

            if (deck.ReleaseDate == default && metadata.ReleaseDate != null)
                deck.ReleaseDate = DateOnly.FromDateTime(metadata.ReleaseDate.Value);

            if (string.IsNullOrEmpty(deck.Description))
                deck.Description = metadata.Description?.Length > 2000 ? metadata.Description?[..2000] : metadata.Description;

            if (metadata.Rating != null)
                deck.ExternalRating = (byte)metadata.Rating;

            foreach (var alias in metadata.Aliases)
            {
                if (deck.Titles.All(t => !string.Equals(t.Title, alias, StringComparison.OrdinalIgnoreCase)))
                    deck.Titles.Add(new DeckTitle { DeckId = deck.DeckId, Title = alias, TitleType = DeckTitleType.Alias });
            }

            MergeDictionaryEntries(deck, metadata.DictionaryEntries);

            await MetadataProviderHelper.ApplyGenreAndTagMappings(context, deck, metadata, LinkType.Mal);

            if (metadata.Relations.Count > 0)
            {
                await MetadataProviderHelper.ProcessRelations(context, deckId, metadata.Relations);
            }

            await context.SaveChangesAsync();
        }
        finally
        {
            await Task.Delay(TimeSpan.FromSeconds(JIKAN_DELAY));
        }
    }

    private static void MergeDictionaryEntries(Deck deck, List<DeckDictionaryEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (deck.DictionaryEntries.All(e => e.Surface != entry.Surface))
            {
                entry.DeckId = deck.DeckId;
                deck.DictionaryEntries.Add(entry);
            }
        }
    }
}