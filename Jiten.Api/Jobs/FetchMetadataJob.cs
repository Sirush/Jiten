using Hangfire;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Core.Data.Providers;
using Jiten.Core.Data.WebNovel;
using Jiten.Core.Services;
using Jiten.Core.WebNovel;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

public class FetchMetadataJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IConfiguration configuration,
    IWebNovelSourceResolver sourceResolver,
    IBackgroundJobClient backgroundJobs,
    ILogger<FetchMetadataJob> logger)
{
    public const float ANILIST_DELAY = 2.2f;
    public const float GOOGLE_BOOKS_DELAY = 3f;
    public const float VNDB_DELAY = 3.5f;
    public const float TMDB_DELAY = 1.5f;
    public const float IGDB_DELAY = 1f;
    public const float JIKAN_DELAY = 1.5f;

    [Queue("anilist")]
    public async Task FetchAnilistMissingMetadata(int deckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        try
        {
            var refetch = await BeginAsync(context, deckId);
            var deck = refetch.Deck;
            if (!ExternalUrlParser.TryParseFirst(deck.Links, LinkType.Anilist, out var parsed))
                throw new Exception($"No usable Anilist work link found for deck with ID {deckId}.");

            var id = int.Parse(parsed.Id);

            var metadata = await MetadataProviderHelper.AnilistApi(id);

            if (metadata == null)
                throw new Exception($"Metadata for Anilist ID {id} not found.");

            if (metadata.Rating != null)
                deck.ExternalRating = (byte)metadata.Rating;

            await MetadataProviderHelper.ApplyProviderMetadataAsync(context, deck, metadata, LinkType.Anilist, refetch.Exclusions);

            await ProcessRelationsAsync(context, refetch, metadata.Relations, logger);

            await CompleteAsync(context, refetch, LinkType.Anilist);
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
            var refetch = await BeginAsync(context, deckId);
            var deck = refetch.Deck;
            if (!ExternalUrlParser.TryParseFirst(deck.Links, LinkType.GoogleBooks, out var parsed))
                throw new Exception($"No usable Google Books work link found for deck with ID {deckId}.");

            var id = parsed.Id;

            var metadata = await MetadataProviderHelper.GoogleBooksApi(id);

            if (metadata == null)
                throw new Exception($"Metadata for Google Books ID {id} not found.");

            if (metadata.Rating != null)
                deck.ExternalRating = (byte)metadata.Rating;

            await MetadataProviderHelper.ApplyProviderMetadataAsync(context, deck, metadata, LinkType.GoogleBooks, refetch.Exclusions);

            await CompleteAsync(context, refetch, LinkType.GoogleBooks);
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
            var refetch = await BeginAsync(context, deckId);
            var deck = refetch.Deck;
            if (!ExternalUrlParser.TryParseFirst(deck.Links, LinkType.Vndb, out var parsed))
                throw new Exception($"No usable VNDB visual novel link found for deck with ID {deckId}.");

            var id = parsed.Id;

            var metadata = await MetadataProviderHelper.VndbApi(id);

            if (metadata == null)
                throw new Exception($"Metadata for VNDB ID {id} not found.");

            if (metadata.Rating != null)
                deck.ExternalRating = (byte)metadata.Rating;

            await MetadataProviderHelper.ApplyProviderMetadataAsync(context, deck, metadata, LinkType.Vndb, refetch.Exclusions);

            await ProcessRelationsAsync(context, refetch, metadata.Relations, logger);

            // VNDB anime adaptations (from the database dump, matched to anime decks by MAL id)
            var animeRelations = MetadataProviderHelper.GetVndbAnimeRelations(id);
            await ProcessRelationsAsync(context, refetch, animeRelations, null);

            await CompleteAsync(context, refetch, LinkType.Vndb);
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
            var refetch = await BeginAsync(context, deckId);
            var deck = refetch.Deck;
            if (!ExternalUrlParser.TryParseFirst(deck.Links, LinkType.Tmdb, out var parsed))
                throw new Exception($"No usable TMDB work link found for deck with ID {deckId}.");

            var id = parsed.Id;

            string apiKey = configuration["TmdbApiKey"]!;

            var isMovie = parsed.Kind switch
            {
                ExternalUrlKind.Movie => true,
                ExternalUrlKind.Tv => false,
                _ => deck.MediaType == MediaType.Movie
            };

            Metadata metadata;
            if (isMovie)
                metadata = await MetadataProviderHelper.TmdbMovieApi(id, apiKey);
            else
                metadata = await MetadataProviderHelper.TmdbTvApi(id, apiKey);

            if (metadata == null)
                throw new Exception($"Metadata for TMDB ID {id} not found.");

            if (metadata.Rating != null)
                deck.ExternalRating = (byte)metadata.Rating;

            await MetadataProviderHelper.ApplyProviderMetadataAsync(context, deck, metadata, LinkType.Tmdb, refetch.Exclusions);

            await CompleteAsync(context, refetch, LinkType.Tmdb);
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
            var refetch = await BeginAsync(context, deckId);
            var deck = refetch.Deck;
            if (!ExternalUrlParser.TryParseFirst(deck.Links, LinkType.Igdb, out var parsed))
                throw new Exception($"No usable IGDB game link found for deck with ID {deckId}.");

            var url = parsed.Id;

            Metadata? metadata = await MetadataProviderHelper.IgdbApi(url,configuration["IgdbClientId"]!, configuration["IgdbClientSecret"]!);
            if (metadata == null)
                throw new Exception($"Metadata for IGDB URL {url} not found.");

            if (metadata.Rating != null)
                deck.ExternalRating = (byte)metadata.Rating;

            await MetadataProviderHelper.ApplyProviderMetadataAsync(context, deck, metadata, LinkType.Igdb, refetch.Exclusions);

            await CompleteAsync(context, refetch, LinkType.Igdb);
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
            var refetch = await BeginAsync(context, deckId);
            var deck = refetch.Deck;
            if (!ExternalUrlParser.TryParseFirst(deck.Links, LinkType.Mal, out var parsed))
                throw new Exception($"No usable MAL work link found for deck with ID {deckId}.");

            var malId = int.Parse(parsed.Id);

            var isAnime = parsed.Kind == ExternalUrlKind.Anime;
            var metadata = isAnime
                ? await MetadataProviderHelper.JikanAnimeApi(malId)
                : await MetadataProviderHelper.JikanMangaApi(malId);

            if (metadata == null)
                throw new Exception($"Metadata for MAL ID {malId} not found.");

            if (metadata.Rating != null)
                deck.ExternalRating = (byte)metadata.Rating;

            await MetadataProviderHelper.ApplyProviderMetadataAsync(context, deck, metadata, LinkType.Mal, refetch.Exclusions);

            await ProcessRelationsAsync(context, refetch, metadata.Relations, logger);

            await CompleteAsync(context, refetch, LinkType.Mal);
        }
        finally
        {
            await Task.Delay(TimeSpan.FromSeconds(JIKAN_DELAY));
        }
    }

    /// <summary>
    /// Refreshes a webnovel deck from its source. <paramref name="provider"/> only routes the job to the site's
    /// queue; <see cref="WebNovelDeckWork.ResolveAsync"/> decides the work fetched.
    /// </summary>
    [WebNovelQueue(WebNovelQueueKind.Metadata)]
    public async Task FetchWebNovelMetadata(WebNovelProvider provider, int deckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var refetch = await BeginAsync(context, deckId);
        var deck = refetch.Deck;

        var work = await WebNovelDeckWork.ResolveAsync(context, deck.DeckId, deck.Links)
                   ?? throw new Exception($"Deck {deck.DeckId} has no webnovel work link.");

        var linkType = WebNovelProviders.Get(work.Provider).LinkType;
        var info = await sourceResolver.Resolve(work.Provider).GetInfoAsync(work.SourceId);
        var metadata = info.ToMetadata();

        if (deck.Links.All(l => l.LinkType != linkType))
            deck.Links.Add(new Link { DeckId = deck.DeckId, LinkType = linkType, Url = info.Url });

        await MetadataProviderHelper.ApplyProviderMetadataAsync(context, deck, metadata, linkType, refetch.Exclusions);

        await CompleteAsync(context, refetch, linkType);
    }

    private sealed record Refetch(Deck Deck, DeckMetadataSnapshot Before, DeckMetadataExclusionSet Exclusions)
    {
        public bool RelationsChanged { get; set; }
    }

    private async Task ProcessRelationsAsync(JitenDbContext context, Refetch refetch, List<MetadataRelation> relations, ILogger? log)
    {
        if (relations.Count == 0)
            return;
        await MetadataProviderHelper.ProcessRelations(context, refetch.Deck.DeckId, relations, log);
        refetch.RelationsChanged = true;
        FranchiseSyncJob.Enqueue(backgroundJobs);
    }

    /// <summary>Loads every navigation <see cref="DeckMetadataSnapshot.Capture"/> reads, so the provider history row sees the whole before state.</summary>
    private static async Task<Refetch> BeginAsync(JitenDbContext context, int deckId)
    {
        var deck = await context.Decks.IncludeSnapshotGraph().FirstAsync(d => d.DeckId == deckId);

        var exclusions = await DeckMetadataExclusionSet.LoadAsync(context, deckId);
        return new Refetch(deck, DeckMetadataSnapshot.Capture(deck), exclusions);
    }

    /// <summary>Saves the refetch with one provider history row when it changed anything; LastUpdate stays put because bulk re-imports gate on it.</summary>
    private static async Task CompleteAsync(JitenDbContext context, Refetch refetch, LinkType provider)
    {
        var deck = refetch.Deck;

        var unloadedTagIds = deck.DeckTags.Where(t => t.Tag == null).Select(t => t.TagId).Distinct().ToList();
        if (unloadedTagIds.Count > 0)
            await context.Tags.Where(t => unloadedTagIds.Contains(t.TagId)).LoadAsync();

        if (refetch.RelationsChanged)
        {
            await context.Entry(deck).Collection(d => d.RelationshipsAsSource).Query().Include(r => r.TargetDeck).LoadAsync();
            await context.Entry(deck).Collection(d => d.RelationshipsAsTarget).Query().Include(r => r.SourceDeck).LoadAsync();
        }

        var edit = MetadataEditLog.ProviderEdit(deck.DeckId, provider, refetch.Before, DeckMetadataSnapshot.Capture(deck));
        if (edit != null)
            context.MetadataEdits.Add(edit);

        await context.SaveChangesAsync();
    }
}
