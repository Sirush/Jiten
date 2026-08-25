namespace Jiten.Core.Data.Providers;

/// <summary>Which catalogue a URL points into, for hosts that serve several under one domain.</summary>
public enum ExternalUrlKind
{
    Unknown = 0,
    Anime,
    Manga,
    Movie,
    Tv
}

/// <param name="Id">The provider's own identifier, in the form its by-id fetcher expects.</param>
public readonly record struct ExternalUrlRef(LinkType LinkType, string Id, ExternalUrlKind Kind);

public static class ExternalUrlParser
{
    public static bool TryParse(string? input, out ExternalUrlRef result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (HostMatches(host, "vndb.org"))
            return TryVndb(segments, out result);

        if (HostMatches(host, "anilist.co"))
            return TryKindedNumeric(segments, LinkType.Anilist, out result);

        if (HostMatches(host, "myanimelist.net"))
            return TryKindedNumeric(segments, LinkType.Mal, out result);

        if (HostMatches(host, "themoviedb.org"))
            return TryTmdb(segments, out result);

        if (HostMatches(host, "igdb.com"))
            return TryIgdb(trimmed, segments, out result);

        if (HostMatches(host, "bookmeter.com"))
            return TryBookmeter(segments, out result);

        if (HostMatches(host, "imdb.com"))
            return TryImdb(segments, out result);

        if (IsGoogleBooksHost(host, uri.AbsolutePath))
            return TryGoogleBooks(uri, segments, out result);

        return false;
    }

    private static bool HostMatches(string host, string domain) =>
        host == domain || host.EndsWith($".{domain}", StringComparison.Ordinal);

    private static bool IsGoogleBooksHost(string host, string path)
    {
        if (host.StartsWith("books.google.", StringComparison.Ordinal))
            return true;

        var isGoogle = host.StartsWith("google.", StringComparison.Ordinal) || host.StartsWith("www.google.", StringComparison.Ordinal);
        return isGoogle && path.Contains("/books/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryVndb(string[] segments, out ExternalUrlRef result)
    {
        result = default;

        if (segments.Length == 0)
            return false;

        var id = segments[0].ToLowerInvariant();

        // Releases, characters and producers share the domain but are not visual novels
        if (id.Length < 2 || id[0] != 'v' || !IsAllDigits(id.AsSpan(1)))
            return false;

        result = new ExternalUrlRef(LinkType.Vndb, id, ExternalUrlKind.Unknown);
        return true;
    }

    private static bool TryKindedNumeric(string[] segments, LinkType linkType, out ExternalUrlRef result)
    {
        result = default;

        if (segments.Length < 2)
            return false;

        var kind = segments[0].ToLowerInvariant() switch
        {
            "anime" => ExternalUrlKind.Anime,
            "manga" => ExternalUrlKind.Manga,
            _ => ExternalUrlKind.Unknown
        };

        if (kind == ExternalUrlKind.Unknown || !IsAllDigits(segments[1]))
            return false;

        result = new ExternalUrlRef(linkType, segments[1], kind);
        return true;
    }

    private static bool TryTmdb(string[] segments, out ExternalUrlRef result)
    {
        result = default;

        if (segments.Length < 2)
            return false;

        var kind = segments[0].ToLowerInvariant() switch
        {
            "movie" => ExternalUrlKind.Movie,
            "tv" => ExternalUrlKind.Tv,
            _ => ExternalUrlKind.Unknown
        };

        if (kind == ExternalUrlKind.Unknown)
            return false;

        // TMDB appends a title slug to the id ("550-fight-club")
        var id = LeadingDigits(segments[1]);
        if (id.Length == 0)
            return false;

        result = new ExternalUrlRef(LinkType.Tmdb, id, kind);
        return true;
    }

    /// <summary>IGDB has no public id lookup, so its own canonical URL is the identifier.</summary>
    private static bool TryIgdb(string url, string[] segments, out ExternalUrlRef result)
    {
        result = default;

        if (segments.Length < 2 || !segments[0].Equals("games", StringComparison.OrdinalIgnoreCase))
            return false;

        result = new ExternalUrlRef(LinkType.Igdb, url.TrimEnd('/'), ExternalUrlKind.Unknown);
        return true;
    }

    private static bool TryBookmeter(string[] segments, out ExternalUrlRef result)
    {
        result = default;

        if (segments.Length < 2 || !segments[0].Equals("books", StringComparison.OrdinalIgnoreCase) || !IsAllDigits(segments[1]))
            return false;

        result = new ExternalUrlRef(LinkType.Bookmeter, segments[1], ExternalUrlKind.Unknown);
        return true;
    }

    private static bool TryImdb(string[] segments, out ExternalUrlRef result)
    {
        result = default;

        if (segments.Length < 2 || !segments[0].Equals("title", StringComparison.OrdinalIgnoreCase))
            return false;

        var id = segments[1].ToLowerInvariant();
        if (id.Length < 3 || !id.StartsWith("tt", StringComparison.Ordinal) || !IsAllDigits(id.AsSpan(2)))
            return false;

        result = new ExternalUrlRef(LinkType.Imdb, id, ExternalUrlKind.Unknown);
        return true;
    }

    private static bool TryGoogleBooks(Uri uri, string[] segments, out ExternalUrlRef result)
    {
        result = default;

        var id = GetQueryValue(uri, "id");

        if (string.IsNullOrEmpty(id))
        {
            // /books/edition/{slug}/{volumeId}
            var last = segments.Length > 0 ? segments[^1] : string.Empty;
            if (segments.Length < 2 || last.Contains('.') || last.Equals("books", StringComparison.OrdinalIgnoreCase))
                return false;

            id = last;
        }

        result = new ExternalUrlRef(LinkType.GoogleBooks, id, ExternalUrlKind.Unknown);
        return true;
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
            return null;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0 || !pair.AsSpan(0, separator).Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        return null;
    }

    private static string LeadingDigits(string value)
    {
        var length = 0;
        while (length < value.Length && char.IsAsciiDigit(value[length]))
            length++;

        return value[..length];
    }

    private static bool IsAllDigits(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return false;

        foreach (var c in value)
        {
            if (!char.IsAsciiDigit(c))
                return false;
        }

        return true;
    }
}
