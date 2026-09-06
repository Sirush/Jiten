using System.Xml.Linq;
using Jiten.Core.Data.YouTube;

namespace Jiten.Core.YouTube;

public record YouTubeFeedEntry(string VideoId, string Title, DateTimeOffset Published);

/// <summary>
/// Reads the public Atom feed for a channel or playlist. Never bot-checked, but only carries the latest ~15
/// videos, so it detects new uploads and nothing else.
/// </summary>
public class YouTubeFeedReader(HttpClient httpClient)
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Yt = "http://www.youtube.com/xml/schemas/2015";

    public async Task<List<YouTubeFeedEntry>> ReadAsync(YouTubeSourceKind kind, string sourceId,
                                                        CancellationToken cancellationToken = default)
    {
        var url = YouTubeUrlParser.FeedUrl(kind, sourceId);
        using var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

        var entries = new List<YouTubeFeedEntry>();
        foreach (var entry in document.Root?.Elements(Atom + "entry") ?? [])
        {
            var videoId = entry.Element(Yt + "videoId")?.Value;
            if (string.IsNullOrEmpty(videoId))
                continue;

            var title = entry.Element(Atom + "title")?.Value ?? videoId;
            var published = DateTimeOffset.TryParse(entry.Element(Atom + "published")?.Value, out var date)
                ? date
                : DateTimeOffset.UtcNow;

            entries.Add(new YouTubeFeedEntry(videoId, title, published));
        }

        return entries;
    }
}
