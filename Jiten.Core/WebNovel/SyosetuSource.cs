using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Jiten.Core.Data.WebNovel;
using Microsoft.Extensions.Logging;

namespace Jiten.Core.WebNovel;

/// <summary>
/// 小説家になろう. Metadata comes from the official API; episode text is scraped.
/// Politeness matches narou.rb's shipped defaults (0.7s between requests, a longer pause every 10).
/// </summary>
public partial class SyosetuSource : IWebNovelSource, IBatchPollableSource
{
    public const string HttpClientName = "webnovel-syosetu";

    /// <summary>
    /// narou.rb download.interval
    /// </summary>
    private static readonly TimeSpan RequestInterval = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// narou.rb download.wait-steps: an extra pause every N requests, Syosetu only
    /// </summary>
    private const int WaitStepEvery = 10;
    private static readonly TimeSpan WaitStepPause = TimeSpan.FromSeconds(5);

    private const int MaxRetries = 4;

    /// <summary>
    /// The API caps a batch query at 500 ncodes
    /// </summary>
    public const int BatchPollChunkSize = 500;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SyosetuSource> _logger;

    private readonly SemaphoreSlim _throttle = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private int _requestCount;

    public WebNovelProvider Provider { get; }

    private bool IsNovel18 => Provider == WebNovelProvider.SyosetuNovel18;
    private string Host => IsNovel18 ? "novel18.syosetu.com" : "ncode.syosetu.com";
    private string ApiUrl => IsNovel18
        ? "https://api.syosetu.com/novel18api/api/"
        : "https://api.syosetu.com/novelapi/api/";

    public SyosetuSource(IHttpClientFactory httpClientFactory, ILogger<SyosetuSource> logger,
                         WebNovelProvider provider = WebNovelProvider.Syosetu)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        Provider = provider;
    }

    public async Task<WebNovelInfo> GetInfoAsync(string sourceId, CancellationToken ct = default)
    {
        var results = await QueryApiAsync([sourceId], ct);

        if (!results.TryGetValue(sourceId, out var info))
            throw new InvalidOperationException($"Syosetu API returned no work for ncode {sourceId}.");

        return info;
    }

    /// <summary>
    /// Polls every tracked novel's update state in ceil(N/500) API calls. Update signal is
    /// general_lastup + general_all_no, so this never touches an episode page.
    /// </summary>
    public async Task<Dictionary<string, WebNovelInfo>> BatchPollAsync(IEnumerable<string> sourceIds,
                                                                       CancellationToken ct = default)
    {
        var all = new Dictionary<string, WebNovelInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in sourceIds.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(BatchPollChunkSize))
        {
            foreach (var (ncode, info) in await QueryApiAsync(chunk, ct))
                all[ncode] = info;
        }

        return all;
    }

    private async Task<Dictionary<string, WebNovelInfo>> QueryApiAsync(IReadOnlyCollection<string> ncodes,
                                                                       CancellationToken ct)
    {
        // gzip=5 and of= keep the response small; docs ask for both on bulk queries
        var url = $"{ApiUrl}?out=json&gzip=5&lim={ncodes.Count}" +
                  "&of=t-n-w-s-g-k-gf-gl-nt-e-ga-l-ir-ist" +
                  $"&ncode={string.Join('-', ncodes)}";

        var json = await FetchGzipJsonAsync(url, ct);

        using var doc = JsonDocument.Parse(json);
        var results = new Dictionary<string, WebNovelInfo>(StringComparer.OrdinalIgnoreCase);

        // First array element is {"allcount":N}; the rest are works
        foreach (var element in doc.RootElement.EnumerateArray().Skip(1))
        {
            var info = ParseInfo(element);
            if (!string.IsNullOrEmpty(info.SourceId))
                results[info.SourceId] = info;
        }

        return results;
    }

    private WebNovelInfo ParseInfo(JsonElement e)
    {
        var ncode = (GetString(e, "ncode") ?? string.Empty).ToLowerInvariant();

        var info = new WebNovelInfo
        {
            Provider = Provider,
            SourceId = ncode,
            Url = WebNovelUrlParser.BuildWorkUrl(Provider, ncode),
            Title = GetString(e, "title") ?? string.Empty,
            Author = GetString(e, "writer"),
            Synopsis = GetString(e, "story")?.Trim(),
            Genre = GenreLabel(GetInt(e, "genre")),
            FirstPublishedAt = ParseJst(GetString(e, "general_firstup")),
            LastUpdatedAt = ParseJst(GetString(e, "general_lastup")),
            EpisodeCount = GetInt(e, "general_all_no"),
            TotalCharacters = GetInt(e, "length"),
            // noveltype 2 = 短編: a single page with no table of contents
            IsOneShot = GetInt(e, "noveltype") == 2,
            // end is 0 for both completed serials and one-shots, 1 while serialising
            IsCompleted = GetInt(e, "end") == 0,
            IsOnHiatus = GetInt(e, "isstop") == 1,
            IsR15 = GetInt(e, "isr15") == 1,
            IsAdultOnly = IsNovel18
        };

        var keywords = GetString(e, "keyword");
        if (!string.IsNullOrWhiteSpace(keywords))
        {
            info.Keywords = keywords
                            .Split([' ', '　'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Distinct()
                            .ToList();
        }

        // A one-shot always has exactly one page (the API usually reports 1, but normalise defensively)
        if (info.IsOneShot)
            info.EpisodeCount = 1;

        return info;
    }

    public async Task<List<WebNovelEpisodeRef>> GetTocAsync(string sourceId, CancellationToken ct = default)
    {
        var info = await GetInfoAsync(sourceId, ct);

        if (info.IsOneShot)
        {
            return
            [
                new WebNovelEpisodeRef
                {
                    Number = 1,
                    Title = info.Title,
                    UpdatedAt = info.LastUpdatedAt,
                    IsOneShot = true
                }
            ];
        }

        var episodes = new List<WebNovelEpisodeRef>();
        var parser = new HtmlParser();
        var page = 1;

        while (true)
        {
            var url = page == 1
                ? $"https://{Host}/{sourceId}/"
                : $"https://{Host}/{sourceId}/?p={page}";

            var html = await FetchStringAsync(url, ct);
            using var document = await parser.ParseDocumentAsync(html, ct);

            var before = episodes.Count;
            string? sectionTitle = null;

            // Chapter headings and episode rows are siblings, so walk them in document order
            foreach (var node in document.QuerySelectorAll(".p-eplist__chapter-title, .p-eplist__sublist"))
            {
                if (node.ClassList.Contains("p-eplist__chapter-title"))
                {
                    sectionTitle = node.TextContent.Trim();
                    continue;
                }

                var link = node.QuerySelector("a.p-eplist__subtitle");
                var href = link?.GetAttribute("href");
                if (link == null || string.IsNullOrEmpty(href))
                    continue;

                var number = EpisodeNumberFromHref(href);
                if (number == null)
                    continue;

                episodes.Add(new WebNovelEpisodeRef
                {
                    Number = number.Value,
                    Title = link.TextContent.Trim(),
                    UpdatedAt = ParseTocUpdate(node.QuerySelector(".p-eplist__update")),
                    SectionTitle = sectionTitle
                });
            }

            if (episodes.Count == before)
                break;

            if (document.QuerySelector("a.c-pager__item--next") == null)
                break;

            page++;
        }

        return episodes.OrderBy(e => e.Number).ToList();
    }

    public async Task<string> GetEpisodeTextAsync(string sourceId, WebNovelEpisodeRef episode,
                                                  CancellationToken ct = default)
    {
        // A one-shot's body lives on the work page itself; /{ncode}/1/ is a 404 for it
        var url = episode.IsOneShot
            ? $"https://{Host}/{sourceId}/"
            : $"https://{Host}/{sourceId}/{episode.Number}/";

        var html = await FetchStringAsync(url, ct);

        var parser = new HtmlParser();
        using var document = await parser.ParseDocumentAsync(html, ct);

        // preface/afterword carry the same js-novel-text class as the body, distinguished by a modifier
        var blocks = document.QuerySelectorAll("div.js-novel-text").ToList();
        var body = blocks.FirstOrDefault(b => !b.ClassList.Contains("p-novel__text--preface") &&
                                              !b.ClassList.Contains("p-novel__text--afterword"));

        if (body == null)
            throw new InvalidOperationException($"No novel text found at {url} (markup may have changed).");

        return ExtractBlockText(body, document);
    }

    private static string ExtractBlockText(IElement block, IDocument document)
    {
        RubyHtmlHelper.InlineRubyAnnotations(block, document);

        var sb = new StringBuilder();
        foreach (var paragraph in block.QuerySelectorAll("p"))
            sb.AppendLine(paragraph.TextContent.Trim());

        // Blank paragraphs are <p><br/></p>; collapse the runs they leave behind
        return BlankLineRuns().Replace(sb.ToString(), "\n\n").Trim();
    }

    private static int? EpisodeNumberFromHref(string href)
    {
        var match = EpisodeHrefPattern().Match(href);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : null;
    }

    /// <summary>
    /// The update cell holds the publish date plus, when the episode was revised, a 改稿 span whose
    /// title attribute carries the revision date. The later of the two is what we track.
    /// </summary>
    private static DateTimeOffset? ParseTocUpdate(IElement? cell)
    {
        if (cell == null)
            return null;

        var revisionSpan = cell.QuerySelector("span[title]")?.GetAttribute("title");
        if (!string.IsNullOrEmpty(revisionSpan))
        {
            var revised = ParseJst(revisionSpan.Replace("改稿", string.Empty).Trim());
            if (revised != null)
                return revised;
        }

        var text = cell.ChildNodes
                       .Where(n => n.NodeType == NodeType.Text)
                       .Select(n => n.TextContent.Trim())
                       .FirstOrDefault(t => !string.IsNullOrEmpty(t));

        return ParseJst(text);
    }

    /// <summary>
    /// Syosetu renders every timestamp in JST with no zone marker. Returned as UTC: these land in
    /// timestamptz columns, and Npgsql only writes DateTimeOffsets whose offset is zero.
    /// </summary>
    private static DateTimeOffset? ParseJst(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string[] formats = ["yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm", "yyyy/MM/dd HH:mm:ss"];

        if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out var parsed))
        {
            return new DateTimeOffset(parsed, TimeSpan.FromHours(9)).ToUniversalTime();
        }

        return null;
    }

    private async Task<string> FetchStringAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// The API's gzip=5 returns a gzip body regardless of Accept-Encoding, so it is inflated by hand.
    /// </summary>
    private async Task<string> FetchGzipJsonAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);

        return await reader.ReadToEndAsync(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            if (IsNovel18)
                request.Headers.Add("Cookie", "over18=yes");

            var response = await client.SendAsync(request, ct);

            // 503 is Syosetu's "Too many access" — back off rather than hammer
            if ((response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                 (int)response.StatusCode == 429) && attempt < MaxRetries)
            {
                response.Dispose();

                var backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                _logger.LogWarning("Syosetu throttled us on {Url}, backing off {Backoff}s (attempt {Attempt})",
                                   url, backoff.TotalSeconds, attempt + 1);
                await Task.Delay(backoff, ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new HttpRequestException($"Request to {url} failed with status {(int)status} ({status}).",
                                               inner: null, statusCode: status);
            }

            return response;
        }
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var sinceLast = DateTimeOffset.UtcNow - _lastRequest;
            if (sinceLast < RequestInterval)
                await Task.Delay(RequestInterval - sinceLast, ct);

            if (++_requestCount % WaitStepEvery == 0)
                await Task.Delay(WaitStepPause, ct);

            _lastRequest = DateTimeOffset.UtcNow;
        }
        finally
        {
            _throttle.Release();
        }
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string? GenreLabel(int code) => code switch
    {
        101 => "異世界〔恋愛〕",
        102 => "現実世界〔恋愛〕",
        201 => "ハイファンタジー〔ファンタジー〕",
        202 => "ローファンタジー〔ファンタジー〕",
        301 => "純文学〔文芸〕",
        302 => "ヒューマンドラマ〔文芸〕",
        303 => "歴史〔文芸〕",
        304 => "推理〔文芸〕",
        305 => "ホラー〔文芸〕",
        306 => "アクション〔文芸〕",
        307 => "コメディー〔文芸〕",
        401 => "VRゲーム〔SF〕",
        402 => "宇宙〔SF〕",
        403 => "空想科学〔SF〕",
        404 => "パニック〔SF〕",
        9901 => "童話〔その他〕",
        9902 => "詩〔その他〕",
        9903 => "エッセイ〔その他〕",
        9904 => "リプレイ〔その他〕",
        9999 => "その他〔その他〕",
        9801 => "ノンジャンル〔ノンジャンル〕",
        _ => null
    };

    [GeneratedRegex(@"/(\d+)/?$")]
    private static partial Regex EpisodeHrefPattern();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLineRuns();
}
