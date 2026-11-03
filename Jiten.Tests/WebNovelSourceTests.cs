using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core.Data.WebNovel;
using Jiten.Core.WebNovel;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jiten.Tests;

/// <summary>Serves canned pages by exact URL and records every request.</summary>
internal sealed class FakeSiteHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _pages = new();

    public List<string> Requests { get; } = new();

    public FakeSiteHandler Page(string url, string html) =>
        Respond(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") });

    public FakeSiteHandler Bytes(string url, byte[] body) =>
        Respond(url, () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

    public FakeSiteHandler Respond(string url, Func<HttpResponseMessage> response)
    {
        _pages[url] = response;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);

        return Task.FromResult(_pages.TryGetValue(url, out var page) ? page() : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

/// <summary>Kakuyomu pages in the shape the live site serves: the Apollo cache inside __NEXT_DATA__.</summary>
internal static class KakuyomuFixtures
{
    public const string WorkId = "16816452219192161720";

    public sealed record Section(string? ChapterTitle, params string[] EpisodeIds);

    public static string WorkPage(string workId, IReadOnlyList<Section> sections, int? publicEpisodeCount = null,
                                  Action<Dictionary<string, object?>>? tweakWork = null)
    {
        var state = new Dictionary<string, object?>();
        var tocRefs = new List<object>();
        var episodeCount = 0;

        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var sectionId = $"9000{i}";
            object? chapterRef = null;

            if (section.ChapterTitle != null)
            {
                state[$"Chapter:{sectionId}"] = new { __typename = "Chapter", id = sectionId, level = 1, title = section.ChapterTitle };
                chapterRef = new Dictionary<string, string> { ["__ref"] = $"Chapter:{sectionId}" };
            }

            foreach (var episodeId in section.EpisodeIds)
            {
                episodeCount++;
                state[$"Episode:{episodeId}"] = new
                {
                    __typename = "Episode", id = episodeId, title = $"第{episodeCount}話",
                    publishedAt = $"2021-03-{10 + episodeCount:D2}T11:29:16.000Z"
                };
            }

            state[$"TableOfContentsChapter:{sectionId}"] = new Dictionary<string, object?>
            {
                ["__typename"] = "TableOfContentsChapter",
                ["id"] = sectionId,
                ["episodeUnions"] = section.EpisodeIds.Select(e => new Dictionary<string, string> { ["__ref"] = $"Episode:{e}" }).ToList(),
                ["chapter"] = chapterRef
            };
            tocRefs.Add(new Dictionary<string, string> { ["__ref"] = $"TableOfContentsChapter:{sectionId}" });
        }

        state["UserAccount:1177354054885111575"] = new
        {
            __typename = "UserAccount", id = "1177354054885111575", name = "kinokoinusuki", activityName = "芦花公園"
        };

        var work = new Dictionary<string, object?>
        {
            ["__typename"] = "Work",
            ["id"] = workId,
            ["title"] = "異端の祝祭",
            ["alternateAuthorName"] = null,
            ["author"] = new Dictionary<string, string> { ["__ref"] = "UserAccount:1177354054885111575" },
            ["adminCoverImageUrl"] = "https://cdn.kdkw.jp/cover_1000/322412/322412000603.webp",
            ["genre"] = "HORROR",
            ["publishedAt"] = "2021-03-22T11:29:16Z",
            ["lastEpisodePublishedAt"] = "2021-05-20T11:29:58Z",
            ["editedAt"] = "2021-05-20T11:31:54Z",
            ["catchphrase"] = "ケエコオ。",
            ["introduction"] = "角川ホラー文庫から刊行予定の作品の前半部です。",
            ["tagLabels"] = new[] { "書籍化", "角川ホラー文庫" },
            ["serialStatus"] = "COMPLETED",
            ["publicEpisodeCount"] = publicEpisodeCount ?? episodeCount,
            ["totalCharacterCount"] = 65042,
            ["isCruel"] = true,
            ["isViolent"] = false,
            ["isSexual"] = false,
            ["tableOfContentsV2"] = tocRefs
        };
        tweakWork?.Invoke(work);
        state[$"Work:{workId}"] = work;

        return Wrap(state, "/works/[workId]");
    }

    public static string PublicationPage(params string[] workIds)
    {
        var state = new Dictionary<string, object?>();
        foreach (var id in workIds)
            state[$"Work:{id}"] = new { __typename = "Work", id, title = "異端の祝祭" };
        state["MediaFranchisedWorkNovelV2:1"] = new { __typename = "MediaFranchisedWorkNovelV2", title = "異端の祝祭" };

        return Wrap(state, "/publication/entry/[...slug]");
    }

    public static string EpisodePage(string bodyParagraphs) =>
        $"""
         <html><body>
         <header><p class="widget-episodeTitle js-vertical-composition-item">①</p></header>
         <div class="widget-episode js-episode-body-container"><div class="widget-episode-inner">
         <div class="widget-episodeBody js-episode-body" data-viewer-history-path="/x">{bodyParagraphs}</div>
         </div></div>
         </body></html>
         """;

    private static string Wrap(Dictionary<string, object?> state, string page)
    {
        var nextData = JsonSerializer.Serialize(new
        {
            props = new { pageProps = new Dictionary<string, object?> { ["__APOLLO_STATE__"] = state } },
            page
        });

        return $"""<!DOCTYPE html><html><head></head><body><div id="__next"></div><script id="__NEXT_DATA__" type="application/json">{nextData}</script></body></html>""";
    }
}

public class KakuyomuSourceTests
{
    private const string WorkUrl = $"https://kakuyomu.jp/works/{KakuyomuFixtures.WorkId}";

    private static (KakuyomuSource Source, FakeSiteHandler Site) Create()
    {
        var site = new FakeSiteHandler();
        var source = new KakuyomuSource(new FakeHttpClientFactory(site), NullLogger<KakuyomuSource>.Instance, WebNovelThrottle.None());
        return (source, site);
    }

    private static readonly KakuyomuFixtures.Section[] TwoChapters =
    [
        new(null, "101"),
        new("第一章：死", "102", "103"),
        new("第二章：苦難", "104")
    ];

    [Fact]
    public async Task Info_MapsTheWorkFields()
    {
        var (source, site) = Create();
        site.Page(WorkUrl, KakuyomuFixtures.WorkPage(KakuyomuFixtures.WorkId, TwoChapters));

        var info = await source.GetInfoAsync(KakuyomuFixtures.WorkId);

        info.Provider.Should().Be(WebNovelProvider.Kakuyomu);
        info.SourceId.Should().Be(KakuyomuFixtures.WorkId);
        info.Url.Should().Be(WorkUrl);
        info.Title.Should().Be("異端の祝祭");
        info.Author.Should().Be("芦花公園");
        info.Synopsis.Should().Be("ケエコオ。\n\n角川ホラー文庫から刊行予定の作品の前半部です。");
        info.Genre.Should().Be("ホラー");
        info.Keywords.Should().Equal("書籍化", "角川ホラー文庫", "残酷描写有り");
        info.EpisodeCount.Should().Be(4);
        info.TotalCharacters.Should().Be(65042);
        info.IsCompleted.Should().BeTrue();
        info.IsOneShot.Should().BeFalse();
        info.IsAdultOnly.Should().BeFalse();
        info.CoverUrl.Should().Be("https://cdn.kdkw.jp/cover_1000/322412/322412000603.webp");
        info.FirstPublishedAt.Should().Be(new DateTimeOffset(2021, 3, 22, 11, 29, 16, TimeSpan.Zero));
        info.LastUpdatedAt.Should().Be(new DateTimeOffset(2021, 5, 20, 11, 29, 58, TimeSpan.Zero));
        info.LastUpdatedAt!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task Info_PrefersThePenNameOverride_AndKeepsUnknownGenres()
    {
        var (source, site) = Create();
        site.Page(WorkUrl, KakuyomuFixtures.WorkPage(KakuyomuFixtures.WorkId, TwoChapters, tweakWork: w =>
        {
            w["alternateAuthorName"] = "別名義";
            w["genre"] = "SOMETHING_NEW";
            w["serialStatus"] = "RUNNING";
            w["adminCoverImageUrl"] = null;
        }));

        var info = await source.GetInfoAsync(KakuyomuFixtures.WorkId);

        info.Author.Should().Be("別名義");
        info.Genre.Should().Be("SOMETHING_NEW");
        info.IsCompleted.Should().BeFalse();
        info.CoverUrl.Should().BeNull();
    }

    [Fact]
    public async Task Toc_ListsEveryEpisodeInReadingOrderWithIdsAndChapters()
    {
        var (source, site) = Create();
        site.Page(WorkUrl, KakuyomuFixtures.WorkPage(KakuyomuFixtures.WorkId, TwoChapters));

        var toc = await source.GetTocAsync(KakuyomuFixtures.WorkId);

        toc.Select(e => (e.Number, e.SourceEpisodeId, e.SectionTitle))
           .Should().Equal((1, "101", null), (2, "102", "第一章：死"), (3, "103", "第一章：死"), (4, "104", "第二章：苦難"));
        toc[0].Title.Should().Be("第1話");
        toc[0].UpdatedAt.Should().Be(new DateTimeOffset(2021, 3, 11, 11, 29, 16, TimeSpan.Zero));
    }

    [Fact]
    public async Task InfoThenToc_FetchTheWorkPageOnce()
    {
        var (source, site) = Create();
        site.Page(WorkUrl, KakuyomuFixtures.WorkPage(KakuyomuFixtures.WorkId, TwoChapters));

        await source.GetInfoAsync(KakuyomuFixtures.WorkId);
        await source.GetTocAsync(KakuyomuFixtures.WorkId);

        site.Requests.Should().Equal(WorkUrl);
    }

    [Fact]
    public async Task Toc_ShorterThanTheReportedCount_Throws()
    {
        var (source, site) = Create();
        site.Page(WorkUrl, KakuyomuFixtures.WorkPage(KakuyomuFixtures.WorkId, TwoChapters, publicEpisodeCount: 9));

        var act = () => source.GetTocAsync(KakuyomuFixtures.WorkId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*lists 4 episodes but reports 9*");
    }

    [Fact]
    public async Task MissingWork_SurfacesThe404()
    {
        var (source, _) = Create();

        var act = () => source.GetInfoAsync("99999999999999999999");

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ChangedMarkup_FailsLoudly()
    {
        var (source, site) = Create();
        site.Page(WorkUrl, "<html><body>maintenance</body></html>");

        var act = () => source.GetInfoAsync(KakuyomuFixtures.WorkId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*__NEXT_DATA__*");
    }

    [Fact]
    public async Task EpisodeText_IsFetchedByEpisodeId_WithRubyInlinedAndBlankRunsCollapsed()
    {
        var (source, site) = Create();
        site.Page($"{WorkUrl}/episodes/102", KakuyomuFixtures.EpisodePage(
                      """
                      <p id="p1">「でそのときあっくんがレミに言ったわけ」</p>
                      <p id="p2" class="blank"><br /></p>
                      <p id="p3" class="blank"><br /></p>
                      <p id="p4" class="blank"><br /></p>
                      <p id="p5">　島本<ruby><rb>笑美</rb><rp>（</rp><rt>えみ</rt><rp>）</rp></ruby>を除いては。</p>
                      <p id="p6"><em class="emphasisDots"><span>そ</span><span>こ</span></em>だけ。</p>
                      """));

        var text = await source.GetEpisodeTextAsync(KakuyomuFixtures.WorkId,
                                                    new WebNovelEpisodeRef { Number = 2, SourceEpisodeId = "102" });

        text.Should().Be("「でそのときあっくんがレミに言ったわけ」\n\n島本{笑美'えみ}を除いては。\nそこだけ。");
        site.Requests.Should().Equal($"{WorkUrl}/episodes/102");
    }

    [Fact]
    public async Task EpisodePageWithoutBody_FailsLoudly()
    {
        var (source, site) = Create();
        site.Page($"{WorkUrl}/episodes/102", "<html><body><p>no body here</p></body></html>");

        var act = () => source.GetEpisodeTextAsync(KakuyomuFixtures.WorkId, new WebNovelEpisodeRef { SourceEpisodeId = "102" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*No novel text*");
    }

    [Fact]
    public async Task BookPage_ResolvesToItsSingleWork()
    {
        var (source, site) = Create();
        site.Page("https://kakuyomu.jp/publication/entry/2024052301", KakuyomuFixtures.PublicationPage(KakuyomuFixtures.WorkId));

        var resolver = new WebNovelSourceResolver([source]);
        var work = await resolver.ResolveUrlAsync("https://kakuyomu.jp/publication/entry/2024052301?from=top");

        work.Should().Be(new WebNovelWorkRef(WebNovelProvider.Kakuyomu, KakuyomuFixtures.WorkId));
    }

    [Theory]
    [InlineData(new string[0], "*doesn't link to a web version*")]
    [InlineData(new[] { "111111111111", "222222222222" }, "*links to 2 works*")]
    public async Task BookPage_WithoutExactlyOneWork_Throws(string[] workIds, string message)
    {
        var (source, site) = Create();
        site.Page("https://kakuyomu.jp/publication/entry/1", KakuyomuFixtures.PublicationPage(workIds));

        var act = () => new WebNovelSourceResolver([source]).ResolveUrlAsync("https://kakuyomu.jp/publication/entry/1");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(message);
    }

    [Fact]
    public async Task Resolver_IgnoresUnrelatedUrls_AndParsesDirectLinksWithoutTheSource()
    {
        var (source, site) = Create();
        var resolver = new WebNovelSourceResolver([source]);

        (await resolver.ResolveUrlAsync("https://example.com/")).Should().BeNull();
        (await resolver.ResolveUrlAsync("https://ncode.syosetu.com/n9669bk/"))
            .Should().Be(new WebNovelWorkRef(WebNovelProvider.Syosetu, "n9669bk"));
        resolver.IsSupported(WebNovelProvider.Syosetu).Should().BeFalse();
        site.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Metadata_LinksTheKakuyomuWork()
    {
        var info = new WebNovelInfo { Provider = WebNovelProvider.Kakuyomu, Url = WorkUrl, Title = "t", Genre = "ホラー" };

        var metadata = info.ToMetadata();

        metadata.Links.Should().ContainSingle().Which.LinkType.Should().Be(Jiten.Core.Data.LinkType.Kakuyomu);
        metadata.Genres.Should().Contain("ホラー");
    }
}

public class SyosetuSourceTests
{
    private const string Ncode = "n1234ab";

    private static (SyosetuSource Source, FakeSiteHandler Site) Create(WebNovelProvider provider = WebNovelProvider.Syosetu)
    {
        var site = new FakeSiteHandler();
        var source = new SyosetuSource(new FakeHttpClientFactory(site), NullLogger<SyosetuSource>.Instance, provider,
                                       WebNovelThrottle.None());
        return (source, site);
    }

    private static byte[] Gzip(string json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
            gzip.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }

    private static string ApiUrl(int count, string ncodes) =>
        $"https://api.syosetu.com/novelapi/api/?out=json&gzip=5&lim={count}&of=t-n-w-s-g-k-gf-gl-nt-e-ga-l-ir-ist&ncode={ncodes}";

    private const string ApiJson =
        """[{"allcount":1},{"title":"テスト","ncode":"N1234AB","writer":"作者","story":"あらすじ","genre":201,"keyword":"R15 異世界","general_firstup":"2020-01-01 10:00:00","general_lastup":"2020-02-01 10:00:00","noveltype":1,"end":1,"general_all_no":2,"length":5000,"isstop":0,"isr15":1}]""";

    [Fact]
    public async Task Toc_GivesEachEpisodeItsPositionAsId_AndTheTextIsFetchedByIt()
    {
        var (source, site) = Create();
        site.Bytes(ApiUrl(1, Ncode), Gzip(ApiJson))
            .Page($"https://ncode.syosetu.com/{Ncode}/",
                  """
                  <div class="p-eplist__chapter-title">第一章</div>
                  <div class="p-eplist__sublist"><a class="p-eplist__subtitle" href="/n1234ab/1/">プロローグ</a><div class="p-eplist__update">2020/01/01 10:00</div></div>
                  <div class="p-eplist__sublist"><a class="p-eplist__subtitle" href="/n1234ab/2/">第一話</a><div class="p-eplist__update">2020/01/02 10:00<span title="2020/03/01 09:00 改稿">（<u>改</u>）</span></div></div>
                  """)
            .Page($"https://ncode.syosetu.com/{Ncode}/2/",
                  """<div class="js-novel-text p-novel__text"><p>本文<ruby>漢字<rp>(</rp><rt>かんじ</rt><rp>)</rp></ruby>です。</p></div>""");

        var info = await source.GetInfoAsync(Ncode);
        var toc = await source.GetTocAsync(Ncode);
        var text = await source.GetEpisodeTextAsync(Ncode, toc[1]);

        info.Title.Should().Be("テスト");
        info.SourceId.Should().Be(Ncode);
        info.Genre.Should().Be("ハイファンタジー〔ファンタジー〕");
        toc.Select(e => (e.Number, e.SourceEpisodeId, e.SectionTitle)).Should().Equal((1, "1", "第一章"), (2, "2", "第一章"));
        toc[1].UpdatedAt.Should().Be(new DateTimeOffset(2020, 3, 1, 0, 0, 0, TimeSpan.Zero));
        text.Should().Be("本文{漢字'かんじ}です。");
    }

    [Fact]
    public async Task Novel18_SendsTheAgeCookie()
    {
        var site = new CookieRecordingHandler();
        var source = new SyosetuSource(new FakeHttpClientFactory(site), NullLogger<SyosetuSource>.Instance,
                                       WebNovelProvider.SyosetuNovel18, WebNovelThrottle.None());

        await source.GetEpisodeTextAsync(Ncode, new WebNovelEpisodeRef { Number = 1, SourceEpisodeId = "1" });

        site.Cookies.Should().ContainSingle().Which.Should().Be("over18=yes");
        site.Urls.Should().Equal($"https://novel18.syosetu.com/{Ncode}/1/");
    }

    private sealed class CookieRecordingHandler : HttpMessageHandler
    {
        public List<string> Cookies { get; } = new();
        public List<string> Urls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            if (request.Headers.TryGetValues("Cookie", out var values))
                Cookies.AddRange(values);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""<div class="js-novel-text"><p>本文</p></div>""")
            });
        }
    }
}
