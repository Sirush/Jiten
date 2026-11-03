using Jiten.Core.Data;
using Jiten.Core.Data.Providers;
using Jiten.Core.WebNovel;
using MetadataProviderHelper = Jiten.Core.MetadataProviderHelper;

namespace Jiten.Api.Services;

/// <summary>Reads a catalogue entry from the provider a link points at.</summary>
public interface IExternalMetadataResolver
{
    /// <returns>Null when the source has no entry for the link.</returns>
    Task<Metadata?> ResolveAsync(ExternalUrlRef link, MediaType? mediaType);

    /// <returns>Null when the source has no entry for the link, or no provider reads it.</returns>
    Task<Metadata?> ResolveAsync(string url, MediaType? mediaType);
}

public class ExternalMetadataResolver(IConfiguration config, IWebNovelSourceResolver webNovelSources) : IExternalMetadataResolver
{
    /// <summary>The provider whose API serves this link, which is also the one whose genre and tag mappings apply.</summary>
    public static bool TryGetProvider(string? url, out LinkType provider)
    {
        if (ExternalUrlParser.TryParse(url, out var parsed))
        {
            // IMDb links are looked up through TMDB
            provider = parsed.LinkType == LinkType.Imdb ? LinkType.Tmdb : parsed.LinkType;
            return true;
        }

        if (WebNovelUrlParser.IsWebNovelLink(url, out var webNovelProvider))
        {
            provider = WebNovelProviders.Get(webNovelProvider).LinkType;
            return true;
        }

        provider = default;
        return false;
    }

    public async Task<Metadata?> ResolveAsync(ExternalUrlRef link, MediaType? mediaType) => link.LinkType switch
    {
        LinkType.Vndb => await MetadataProviderHelper.VndbApi(link.Id),
        LinkType.GoogleBooks => await MetadataProviderHelper.GoogleBooksApi(link.Id),
        LinkType.Igdb => await MetadataProviderHelper.IgdbApi(link.Id, config["IgdbClientId"]!, config["IgdbClientSecret"]!),
        LinkType.Anilist => link.Kind == ExternalUrlKind.Anime
            ? await MetadataProviderHelper.AnilistAnimeApi(int.Parse(link.Id))
            : await MetadataProviderHelper.AnilistApi(int.Parse(link.Id)),
        LinkType.Mal => link.Kind == ExternalUrlKind.Anime
            ? await MetadataProviderHelper.JikanAnimeApi(int.Parse(link.Id))
            : await MetadataProviderHelper.JikanMangaApi(int.Parse(link.Id)),
        LinkType.Tmdb => IsTmdbMovie(link.Kind, mediaType)
            ? await MetadataProviderHelper.TmdbMovieApi(link.Id, config["TmdbApiKey"]!)
            : await MetadataProviderHelper.TmdbTvApi(link.Id, config["TmdbApiKey"]!),
        LinkType.Imdb => await MetadataProviderHelper.TmdbFindByImdbApi(link.Id, config["TmdbApiKey"]!, IsTmdbMovie(link.Kind, mediaType)),
        LinkType.Bookmeter => await MetadataProviderHelper.BookmeterApi(link.Id),
        _ => null
    };

    public async Task<Metadata?> ResolveAsync(string url, MediaType? mediaType)
    {
        if (ExternalUrlParser.TryParse(url, out var parsed))
            return await ResolveAsync(parsed, mediaType);

        if (!WebNovelUrlParser.IsWebNovelLink(url, out var provider) || !webNovelSources.IsSupported(provider))
            return null;

        var work = await webNovelSources.ResolveUrlAsync(url);
        return work == null ? null : (await webNovelSources.Resolve(work.Provider).GetInfoAsync(work.SourceId)).ToMetadata();
    }

    private static bool IsTmdbMovie(ExternalUrlKind kind, MediaType? mediaType) => kind switch
    {
        ExternalUrlKind.Movie => true,
        ExternalUrlKind.Tv => false,
        _ => mediaType == MediaType.Movie
    };
}
