using AngleSharp.Dom;
using Ganss.Xss;

namespace Jiten.Api.Services.Forum;

/// <summary>Allow-list for Discourse's posts HTML, which the site renders with v-html; Discourse sanitises it too, this is the second layer.</summary>
public class ForumPostSanitizer
{
    private const string LinkRel = "nofollow noopener noreferrer ugc";

    private readonly string _baseUrl;
    private readonly HashSet<string> _imageHosts;
    private readonly HtmlSanitizer _sanitizer;

    /// <param name="forumUrl">Relative links and image sources resolve against it, and its host may serve images.</param>
    /// <param name="mediaHosts">Other hosts images may load from. An image anywhere else becomes a link, so the poster can't log readers' IPs.</param>
    public ForumPostSanitizer(string forumUrl, IEnumerable<string> mediaHosts)
    {
        _baseUrl = forumUrl.TrimEnd('/') + "/";
        _imageHosts = new HashSet<string>(mediaHosts, StringComparer.OrdinalIgnoreCase);
        if (Uri.TryCreate(_baseUrl, UriKind.Absolute, out var forum)) _imageHosts.Add(forum.Host);
        _sanitizer = Create();
    }

    public string Sanitize(string cooked) => _sanitizer.Sanitize(cooked, _baseUrl);

    private bool IsOwnImage(string? src) =>
        Uri.TryCreate(src, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && _imageHosts.Contains(uri.Host);

    private HtmlSanitizer Create()
    {
        var sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "p", "br", "hr", "a", "img", "strong", "b", "em", "i", "s", "del", "ins", "u", "sub", "sup", "small", "mark",
                "code", "pre", "kbd", "blockquote", "aside", "div", "span", "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6",
                "details", "summary", "table", "thead", "tbody", "tr", "th", "td", "header", "article"
            },
            AllowedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "href", "src", "alt", "title", "width", "height", "class", "open"
            },
            UriAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "href", "src" },
            AllowedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" },
            AllowedCssClasses = new HashSet<string>(StringComparer.Ordinal)
            {
                "emoji", "mention", "hashtag-cooked", "quote", "title", "avatar", "spoiler", "spoiled",
                "onebox", "onebox-body", "source", "thumbnail", "site-icon", "lightbox-wrapper", "meta"
            }
        });

        sanitizer.FilterUrl += (_, e) =>
        {
            if (e.OriginalUrl.StartsWith('#')) e.SanitizedUrl = null;
        };

        sanitizer.PostProcessDom += (_, e) =>
        {
            foreach (var meta in e.Document.QuerySelectorAll(".lightbox-wrapper .meta").ToList())
                meta.Remove();

            foreach (var image in e.Document.QuerySelectorAll("img").ToList())
            {
                var src = image.GetAttribute("src");
                if (IsOwnImage(src)) continue;

                if (string.IsNullOrEmpty(src) || image.Closest("a") != null)
                {
                    image.Remove();
                    continue;
                }

                var alt = image.GetAttribute("alt");
                var link = e.Document.CreateElement("a");
                link.SetAttribute("href", src);
                link.TextContent = string.IsNullOrWhiteSpace(alt) ? "Image" : alt;
                image.Replace(link);
            }

            foreach (var link in e.Document.QuerySelectorAll("a"))
            {
                link.SetAttribute("target", "_blank");
                link.SetAttribute("rel", LinkRel);
            }

            foreach (var image in e.Document.QuerySelectorAll("img"))
                image.SetAttribute("loading", "lazy");
        };

        return sanitizer;
    }
}
