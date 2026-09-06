using System.Text.RegularExpressions;
using Jiten.Core.Data.YouTube;

namespace Jiten.Core.YouTube;

/// <summary>
/// Turns whatever an admin pastes (channel, handle, playlist or video URL, or a bare id) into a canonical
/// source URL yt-dlp can enumerate. Handles still need yt-dlp to resolve to a UC id.
/// </summary>
public static partial class YouTubeUrlParser
{
    [GeneratedRegex(@"^UC[\w-]{22}$")]
    private static partial Regex ChannelIdPattern();

    [GeneratedRegex(@"^(PL|UU|OL|FL|RD)[\w-]{10,}$")]
    private static partial Regex PlaylistIdPattern();

    [GeneratedRegex(@"^[\w-]{11}$")]
    private static partial Regex VideoIdPattern();

    public static bool TryParse(string input, out YouTubeSourceKind kind, out string listingUrl, out string? knownId)
    {
        kind = default;
        listingUrl = string.Empty;
        knownId = null;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        input = input.Trim();

        if (ChannelIdPattern().IsMatch(input))
        {
            kind = YouTubeSourceKind.Channel;
            knownId = input;
            listingUrl = ChannelVideosUrl(input);
            return true;
        }

        if (PlaylistIdPattern().IsMatch(input))
        {
            kind = YouTubeSourceKind.Playlist;
            knownId = input;
            listingUrl = PlaylistUrl(input);
            return true;
        }

        if (input.StartsWith('@'))
        {
            kind = YouTubeSourceKind.Channel;
            listingUrl = $"https://www.youtube.com/{input}/videos";
            return true;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host.ToLowerInvariant();
        if (host is not ("www.youtube.com" or "youtube.com" or "m.youtube.com" or "youtu.be"))
            return false;

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var list = query["list"];
        if (!string.IsNullOrEmpty(list) && PlaylistIdPattern().IsMatch(list))
        {
            kind = YouTubeSourceKind.Playlist;
            knownId = list;
            listingUrl = PlaylistUrl(list);
            return true;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        switch (segments[0])
        {
            case "channel" when segments.Length >= 2 && ChannelIdPattern().IsMatch(segments[1]):
                kind = YouTubeSourceKind.Channel;
                knownId = segments[1];
                listingUrl = ChannelVideosUrl(segments[1]);
                return true;
            case "c" or "user" when segments.Length >= 2:
                kind = YouTubeSourceKind.Channel;
                listingUrl = $"https://www.youtube.com/{segments[0]}/{segments[1]}/videos";
                return true;
            case var handle when handle.StartsWith('@'):
                kind = YouTubeSourceKind.Channel;
                listingUrl = $"https://www.youtube.com/{handle}/videos";
                return true;
            default:
                return false;
        }
    }

    public static bool IsVideoId(string? input) => input != null && VideoIdPattern().IsMatch(input);

    public static bool TryParseVideoId(string input, out string videoId)
    {
        videoId = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        input = input.Trim();
        if (VideoIdPattern().IsMatch(input))
        {
            videoId = input;
            return true;
        }

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return false;

        if (uri.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            var id = uri.AbsolutePath.Trim('/');
            if (!VideoIdPattern().IsMatch(id))
                return false;
            videoId = id;
            return true;
        }

        var v = System.Web.HttpUtility.ParseQueryString(uri.Query)["v"];
        if (!string.IsNullOrEmpty(v) && VideoIdPattern().IsMatch(v))
        {
            videoId = v;
            return true;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 2 && segments[0] is "shorts" or "live" or "embed" && VideoIdPattern().IsMatch(segments[1]))
        {
            videoId = segments[1];
            return true;
        }

        return false;
    }

    public static string ChannelVideosUrl(string channelId) => $"https://www.youtube.com/channel/{channelId}/videos";
    public static string ChannelUrl(string channelId) => $"https://www.youtube.com/channel/{channelId}";
    public static string PlaylistUrl(string playlistId) => $"https://www.youtube.com/playlist?list={playlistId}";
    public static string VideoUrl(string videoId) => $"https://www.youtube.com/watch?v={videoId}";

    public static string SourceUrl(YouTubeSourceKind kind, string sourceId) => kind == YouTubeSourceKind.Channel
        ? ChannelUrl(sourceId)
        : PlaylistUrl(sourceId);

    public static string FeedUrl(YouTubeSourceKind kind, string sourceId) => kind == YouTubeSourceKind.Channel
        ? $"https://www.youtube.com/feeds/videos.xml?channel_id={sourceId}"
        : $"https://www.youtube.com/feeds/videos.xml?playlist_id={sourceId}";
}
