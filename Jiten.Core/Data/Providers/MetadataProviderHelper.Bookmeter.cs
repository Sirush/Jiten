using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Jiten.Core.Data;
using Jiten.Core.Data.Providers;

namespace Jiten.Core;

public static partial class MetadataProviderHelper
{
    private static readonly Regex BookmeterAsinRegex =
        new(@"amazon\.co\.jp/dp/([A-Z0-9]+)", RegexOptions.Compiled);

    // Clean title from og:title: 『タイトル』｜... - 読書メーター
    private static readonly Regex BookmeterOgTitleRegex =
        new(@"[『｢]([^』｣]+)[』｣]", RegexOptions.Compiled);

    // あらすじ (synopsis) field inside bm-details-side
    private static readonly Regex BookmeterDescRegex =
        new(@"<dt[^>]*>\s*あらすじ\s*</dt>\s*<dd[^>]*>(.*?)</dd>",
            RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex BookmeterRatingRegex =
        new(@"supplement__value average"">(\d+)</span>", RegexOptions.Compiled);

    private static readonly Regex HtmlTagRegex =
        new(@"<[^>]+>", RegexOptions.Compiled);

    public static async Task<List<Metadata>> BookmeterSearchApi(string query)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        var escapedQuery = Uri.EscapeDataString(query);
        var searchUrl1 = $"https://bookmeter.com/search?keyword={escapedQuery}&partial=true&sort=recommended&type=japanese_v2";
        var searchUrl2 = $"https://bookmeter.com/search?author=&keyword={escapedQuery}&partial=true&sort=recommended&type=japanese&page=1";

        var searchResults = await Task.WhenAll(
            BookmeterFetchSearchItems(http, searchUrl1),
            BookmeterFetchSearchItems(http, searchUrl2));

        var seen = new HashSet<string>();
        var combined = searchResults[0].Concat(searchResults[1])
            .Where(x => seen.Add(x.BookPath))
            .ToList();

        var metadatas = await Task.WhenAll(combined.Select(async item =>
        {
            var bookmeterUrl = $"https://bookmeter.com{item.BookPath}";
            List<Link> links = [new Link { LinkType = LinkType.Bookmeter, Url = bookmeterUrl }];

            var details = await BookmeterGetDetails(http, bookmeterUrl);

            if (details.Asin != null)
                links.Add(new Link { LinkType = LinkType.Amazon, Url = $"https://www.amazon.co.jp/dp/{details.Asin}" });

            return new Metadata
            {
                OriginalTitle = details.CleanTitle ?? item.FallbackTitle,
                Image = item.ImageUrl,
                Links = links,
                Description = details.Description,
                Rating = details.Rating,
                ReleaseDate = details.ReleaseDate
            };
        }));

        return metadatas.OfType<Metadata>().ToList();
    }

    private record BookmeterSearchItem(string BookPath, string FallbackTitle, string ImageUrl);

    private static async Task<List<BookmeterSearchItem>> BookmeterFetchSearchItems(HttpClient http, string url)
    {
        try
        {
            var response = await http.GetAsync(url);
            if (!response.IsSuccessStatusCode) return [];

            var html = await response.Content.ReadAsStringAsync();
            var document = await new HtmlParser().ParseDocumentAsync(html);

            return document.QuerySelectorAll("li.group__book")
                .SelectMany(item =>
                {
                    var coverImg = item.QuerySelector("img.cover__image");
                    var bookLink = item.QuerySelector("div.thumbnail__cover a");
                    if (coverImg == null || bookLink == null) return Enumerable.Empty<BookmeterSearchItem>();

                    var bookPath = bookLink.GetAttribute("href") ?? "";
                    if (string.IsNullOrEmpty(bookPath)) return Enumerable.Empty<BookmeterSearchItem>();

                    return [new BookmeterSearchItem(
                        bookPath,
                        coverImg.GetAttribute("alt") ?? "",
                        coverImg.GetAttribute("src") ?? "")];
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private record BookmeterDetails(
        string? CleanTitle,
        string? Asin,
        string? Description,
        int? Rating,
        DateTime? ReleaseDate);

    private static async Task<BookmeterDetails> BookmeterGetDetails(HttpClient http, string bookmeterUrl)
    {
        try
        {
            var response = await http.GetAsync(bookmeterUrl);
            if (!response.IsSuccessStatusCode) return new(null, null, null, null, null);

            var html = await response.Content.ReadAsStringAsync();

            // Clean title from og:title: 『タイトル』｜... - 読書メーター
            string? cleanTitle = null;
            var ogTitleMatch = Regex.Match(html, @"property=""og:title"" content=""([^""]+)""");
            if (ogTitleMatch.Success)
            {
                var m = BookmeterOgTitleRegex.Match(ogTitleMatch.Groups[1].Value);
                if (m.Success) cleanTitle = m.Groups[1].Value.Trim();
            }

            var asin = BookmeterAsinRegex.Match(html) is { Success: true } am
                ? am.Groups[1].Value
                : null;

            // あらすじ field in bm-details-side
            string? description = null;
            if (BookmeterDescRegex.Match(html) is { Success: true } dm)
            {
                var inner = dm.Groups[1].Value
                    .Replace("<br>", "\n").Replace("<br />", "\n").Replace("<br/>", "\n");
                var plain = HtmlTagRegex.Replace(inner, "");
                plain = WebUtility.HtmlDecode(plain)
                    .Replace("あらすじ・内容をもっと見る", "")
                    .Trim();
                if (!string.IsNullOrWhiteSpace(plain))
                    description = plain;
            }

            int? rating = BookmeterRatingRegex.Match(html) is { Success: true } rm
                          && int.TryParse(rm.Groups[1].Value, out var r)
                ? r
                : null;

            DateTime? releaseDate = asin != null
                ? await OpenBdGetPubdate(http, asin)
                : null;

            return new(cleanTitle, asin, description, rating, releaseDate);
        }
        catch
        {
            return new(null, null, null, null, null);
        }
    }

    private static async Task<DateTime?> OpenBdGetPubdate(HttpClient http, string isbn)
    {
        try
        {
            var response = await http.GetAsync($"https://api.openbd.jp/v1/get?isbn={isbn}");
            if (!response.IsSuccessStatusCode) return null;

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
            var first = root[0];
            if (first.ValueKind == JsonValueKind.Null) return null;

            if (!first.TryGetProperty("summary", out var summary)) return null;
            if (!summary.TryGetProperty("pubdate", out var pubdateEl)) return null;

            var pubdate = pubdateEl.GetString();
            if (string.IsNullOrEmpty(pubdate) || pubdate.Length < 6) return null;

            // Format: YYYYMM or YYYYMMDD
            if (int.TryParse(pubdate[..4], out var year) && int.TryParse(pubdate.Substring(4, 2), out var month))
            {
                var day = pubdate.Length >= 8 && int.TryParse(pubdate.Substring(6, 2), out var d) ? d : 1;
                return new DateTime(year, month, day);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
