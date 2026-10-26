using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Api.Services.Forum;
using Jiten.Core.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jiten.Tests;

public class ForumDiscussionTests
{
    private const string Forum = "https://community.jiten.moe";
    private const string EmbedUrl = "https://jiten.moe/lists/12";
    private const string MediaHost = "jiten-community.b-cdn.net";
    private static readonly ForumPostSanitizer Sanitizer = new(Forum, [MediaHost]);
    private static readonly ForumTopicDraft Draft = new(ForumSubject.List, EmbedUrl, "My list", EmbedUrl);

    private static ForumTopicDraft DeckDraft(int id) => new(ForumSubject.Deck, $"https://jiten.moe/decks/media/{id}/detail", $"Deck {id}", "");

    private static List<List<int>> Threads(params (int Number, int? ReplyTo)[] posts) =>
        ForumDiscussionService.BuildThreads(posts, p => p.Number, p => p.ReplyTo).Select(t => t.Select(p => p.Number).ToList()).ToList();

    [Fact]
    public void BuildThreads_GroupsRepliesUnderTheirRootInPostingOrder()
    {
        Threads((2, null), (3, 2), (4, null), (5, 3), (6, 2)).Should().BeEquivalentTo(
            new List<List<int>> { new() { 2, 3, 5, 6 }, new() { 4 } }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void BuildThreads_OrdersThreadsByLatestActivity()
    {
        Threads((2, null), (3, null), (4, 2)).Select(t => t[0]).Should().Equal(2, 3);
    }

    [Fact]
    public void BuildThreads_RepliesToTheTopicOrToMissingPostsStartTheirOwnThread()
    {
        Threads((2, 1), (3, 99)).Select(t => t[0]).Should().Equal(3, 2);
    }

    [Fact]
    public void SelectThreadPage_ContinuesAfterTheCursor()
    {
        int[] lastPostNumbers = [9, 7, 5, 3];

        var (first, next) = ForumDiscussionService.SelectThreadPage(lastPostNumbers, n => n, null, 2);
        first.Should().Equal(9, 7);
        next.Should().Be(7);

        var (second, end) = ForumDiscussionService.SelectThreadPage(lastPostNumbers, n => n, next, 2);
        second.Should().Equal(5, 3);
        end.Should().BeNull();
    }

    [Fact]
    public void Sanitize_RemovesScriptsHandlersStylesAndScriptUrls()
    {
        var html = Sanitizer.Sanitize(
            """<p onclick="x()" style="color:red">hi<script>alert(1)</script></p><a href="javascript:alert(1)">bad</a><iframe src="https://evil.example"></iframe>""");

        html.Should().NotContainAny("onclick", "style=", "<script", "javascript:", "<iframe");
        html.Should().Contain("hi");
    }

    [Fact]
    public void Sanitize_MakesForumLinksAbsoluteAndOpensThemInANewTab()
    {
        var html = Sanitizer.Sanitize("""<a class="mention" href="/u/sirus">@Sirus</a><img src="/uploads/a.png" class="emoji other">""");

        html.Should().Contain("""href="https://community.jiten.moe/u/sirus""");
        html.Should().Contain("""src="https://community.jiten.moe/uploads/a.png""");
        html.Should().Contain("""target="_blank""").And.Contain("noopener");
        html.Should().Contain("""class="mention""").And.Contain("""class="emoji""");
        html.Should().NotContain("other");
    }

    [Fact]
    public void Sanitize_DropsInPageAnchorsThatWouldPointAtTheForumHome()
    {
        var html = Sanitizer.Sanitize("""<h2><a name="p-1" class="anchor" href="#p-1"></a>Title</h2>""");

        html.Should().NotContain("href").And.Contain("Title");
    }

    [Fact]
    public void Sanitize_DropsLightboxMetadata()
    {
        var html = Sanitizer.Sanitize(
            """<div class="lightbox-wrapper"><a class="lightbox" href="/uploads/a.png"><img src="/uploads/a.png"><div class="meta"><span>image 1920×1080 120 KB</span></div></a></div>""");

        html.Should().Contain("<img").And.NotContain("1920");
    }

    [Fact]
    public async Task Get_IsUnavailableWithoutAnApiKey()
    {
        var forum = new FakeForum();
        var service = forum.Service(apiKey: "");

        (await service.GetAsync(EmbedUrl, null, CancellationToken.None)).Status.Should().Be(ForumDiscussionStatus.Unavailable);
        forum.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_LoadsImagesOnlyFromTheForumAndItsMediaHosts()
    {
        var html = Sanitizer.Sanitize(
            $"""<img src="https://{MediaHost}/a.png" alt="ok"><img src="https://tracker.example/pixel.png" alt="Their pic"><img src="https://tracker.example/x.png">""");

        html.Should().Contain($"""<img src="https://{MediaHost}/a.png""");
        html.Should().Contain("""<a href="https://tracker.example/pixel.png" target="_blank" rel="nofollow noopener noreferrer ugc">Their pic</a>""");
        html.Should().Contain(">Image</a>");
        html.Should().NotContain("<img src=\"https://tracker.example");
    }

    [Fact]
    public async Task Get_MissingTopicIsNotStartedAndCreatesNothing()
    {
        var forum = new FakeForum();
        var service = forum.Service();

        (await service.GetAsync(EmbedUrl, null, CancellationToken.None)).Status.Should().Be(ForumDiscussionStatus.NotStarted);
        (await service.GetAsync(EmbedUrl, null, CancellationToken.None)).Status.Should().Be(ForumDiscussionStatus.NotStarted);

        forum.Requests.Should().ContainSingle(r => r.Path == "/embed/info");
        forum.Requests.Should().NotContain(r => r.Path == "/posts.json");
    }

    [Fact]
    public async Task Start_CreatesTheTopicOnceAndShowsIt()
    {
        var forum = new FakeForum();
        forum.Posts.Add(Post(1, 1));
        var service = forum.Service(categories: new() { [ForumSubject.List] = 7 }, topicWriteKey: "write");
        await service.GetAsync(EmbedUrl, null, CancellationToken.None);

        (await service.StartAsync(Draft, CancellationToken.None)).Status.Should().Be(ForumDiscussionStatus.Ready);
        (await service.StartAsync(Draft, CancellationToken.None)).Status.Should().Be(ForumDiscussionStatus.Ready);
        (await service.GetAsync(EmbedUrl, null, CancellationToken.None)).TopicUrl.Should().Be("https://community.jiten.moe/t/a-topic/43");

        var creation = forum.Requests.Should().ContainSingle(r => r.Path == "/posts.json").Subject;
        creation.Method.Should().Be(HttpMethod.Post);
        creation.ApiKey.Should().Be("write");
        forum.Requests.Where(r => r.Path == "/embed/info").Should().OnlyContain(r => r.ApiKey == "key");
        creation.ApiUsername.Should().Be("system");
        var body = JsonDocument.Parse(creation.Body!).RootElement;
        body.GetProperty("title").GetString().Should().Be("My list");
        body.GetProperty("embed_url").GetString().Should().Be(EmbedUrl);
        body.GetProperty("category").GetInt32().Should().Be(7);
        body.GetProperty("skip_validations").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Start_WithoutACategoryLeavesItToTheForum()
    {
        var forum = new FakeForum();

        await forum.Service(categories: new() { [ForumSubject.List] = 7 }).StartAsync(DeckDraft(1), CancellationToken.None);

        JsonDocument.Parse(forum.Requests.Single(r => r.Path == "/posts.json").Body!).RootElement.TryGetProperty("category", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Start_RefusedCreationFindsATopicCreatedElsewhere()
    {
        var forum = new FakeForum { CreateStatus = HttpStatusCode.UnprocessableEntity, TopicIdAfterRefusal = 43 };
        forum.Posts.AddRange([Post(1, 1), Post(2, 2)]);

        var result = await forum.Service().StartAsync(Draft, CancellationToken.None);

        result.Status.Should().Be(ForumDiscussionStatus.Ready);
        result.Threads.Should().ContainSingle();
    }

    [Fact]
    public async Task Start_ExistingTopicIsReturnedWithoutCreating()
    {
        var forum = new FakeForum { TopicId = 43 };
        forum.Posts.AddRange([Post(1, 1), Post(2, 2)]);

        var result = await forum.Service().StartAsync(Draft, CancellationToken.None);

        result.Status.Should().Be(ForumDiscussionStatus.Ready);
        result.Threads.Should().ContainSingle();
        forum.Requests.Should().NotContain(r => r.Path == "/posts.json");
    }

    [Theory]
    [InlineData("シュタインズ・ゲート", "Steins;Gate", "Steins;Gate (シュタインズ・ゲート)")]
    [InlineData("ゆるキャン△", null, "ゆるキャン△")]
    [InlineData("Clannad", " clannad ", "Clannad")]
    public void DeckTitle_PutsRomajiFirstAndTheOriginalAfterWhenItDiffers(string original, string? romaji, string expected)
    {
        ForumDiscussionSubjects.DeckTitle(original, romaji).Should().Be(expected);
    }

    [Fact]
    public void Body_PutsTheUrlAloneOnTheFirstLineAndQuotesTheDescription()
    {
        ForumDiscussionSubjects.Body(EmbedUrl, "one\r\ntwo").Should().Be($"{EmbedUrl}\n\n> one\n> two");
        ForumDiscussionSubjects.Body(EmbedUrl, "  ").Should().Be(EmbedUrl);
    }

    [Fact]
    public async Task KeyedCallsStayWithinTheirBudgets()
    {
        var forum = new FakeForum();
        var service = forum.Service(lookups: 3, creations: 2);

        for (var i = 0; i < 5; i++)
            await service.GetAsync($"https://jiten.moe/decks/media/{i}/detail", null, CancellationToken.None);
        for (var i = 10; i < 15; i++)
            await service.StartAsync(DeckDraft(i), CancellationToken.None);

        forum.Requests.Count(r => r.Path == "/embed/info").Should().Be(3);
        forum.Requests.Count(r => r.Path == "/posts.json").Should().Be(0);

        var fresh = new FakeForum { CreateStatus = HttpStatusCode.UnprocessableEntity };
        var starter = fresh.Service(lookups: 20, creations: 2);
        for (var i = 0; i < 5; i++)
            await starter.StartAsync(DeckDraft(i), CancellationToken.None);
        fresh.Requests.Count(r => r.Path == "/posts.json").Should().Be(2);
    }

    [Fact]
    public async Task Get_SkipsPostsByDeletedAccounts()
    {
        var forum = new FakeForum { TopicId = 43 };
        forum.Posts.AddRange([Post(1, 1), Post(2, 2), Post(3, 3, deletedAccount: true)]);

        var result = await forum.Service().GetAsync(EmbedUrl, null, CancellationToken.None);

        result.Status.Should().Be(ForumDiscussionStatus.Ready);
        result.Threads.Select(t => t.Post.PostId).Should().Equal(2);
    }

    [Fact]
    public async Task Get_RateLimitedForumPausesEveryCall()
    {
        var forum = new FakeForum { TopicId = 43, TopicStatus = HttpStatusCode.TooManyRequests };
        var service = forum.Service();

        (await service.GetAsync(EmbedUrl, null, CancellationToken.None)).Status.Should().Be(ForumDiscussionStatus.Unavailable);
        var sent = forum.Requests.Count;
        (await service.GetAsync("https://jiten.moe/lists/13", null, CancellationToken.None)).Status.Should().Be(ForumDiscussionStatus.Unavailable);

        forum.Requests.Count.Should().Be(sent);
    }

    [Fact]
    public async Task Get_ConcurrentViewsShareOneTopicRead()
    {
        var forum = new FakeForum { TopicId = 43, Delay = TimeSpan.FromMilliseconds(200) };
        forum.Posts.AddRange([Post(1, 1), Post(2, 2)]);
        var service = forum.Service();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.GetByTopicIdAsync(43, null, CancellationToken.None)));

        results.Should().OnlyContain(r => r.Status == ForumDiscussionStatus.Ready);
        forum.Requests.Count(r => r.Path == "/t/43.json").Should().Be(1);
    }

    [Fact]
    public async Task Get_ThreadsVisibleRepliesFromAnAnonymousRead()
    {
        var forum = new FakeForum { TopicId = 43 };
        forum.Posts.AddRange([
            Post(1, 1),
            Post(2, 2),
            Post(3, 3, postType: 3),
            Post(4, 4, hidden: true),
            Post(5, 5, replyToNumber: 2, replyToUser: "u2"),
            Post(6, 6, replyToNumber: 5, replyToUser: "u5"),
            Post(7, 7, replyToNumber: 4, replyToUser: "u4")
        ]);

        var result = await forum.Service().GetAsync(EmbedUrl, null, CancellationToken.None);

        result.Status.Should().Be(ForumDiscussionStatus.Ready);
        result.TopicUrl.Should().Be("https://community.jiten.moe/t/a-topic/43");
        result.ReplyCount.Should().Be(5);
        result.Threads.Select(t => t.Post.PostId).Should().Equal(7, 2);
        result.Threads[0].Post.ReplyToUsername.Should().Be("u4");
        result.Threads[1].Replies.Select(p => p.PostId).Should().Equal(5, 6);
        result.Threads[1].Replies.Select(p => p.ReplyToUsername).Should().Equal(null, "u5");
        result.Threads[1].Post.Url.Should().Be("https://community.jiten.moe/t/a-topic/43/2");
        result.Threads[1].Post.AvatarUrl.Should().Be("https://community.jiten.moe/user_avatar/community.jiten.moe/u2/96/1.png");
        result.NextBefore.Should().BeNull();

        forum.Requests.Should().ContainSingle(r => r.Path == "/t/43.json").Which.ApiKey.Should().BeNull();
    }

    [Fact]
    public async Task Get_ReadsRepliesBeyondTheTopicsFirstPostsAndPagesThroughThreads()
    {
        var forum = new FakeForum { TopicId = 43, PostsInTopicResponse = 20 };
        forum.Posts.AddRange(Enumerable.Range(1, 30).Select(i => Post(i, i)));
        var service = forum.Service();

        var first = await service.GetAsync(EmbedUrl, null, CancellationToken.None);
        first.Threads.Select(t => t.Post.PostId).Should().Equal(Enumerable.Range(21, 10).Reverse());
        first.NextBefore.Should().Be(21);

        var second = await service.GetAsync(EmbedUrl, first.NextBefore, CancellationToken.None);
        second.Threads.Select(t => t.Post.PostId).Should().Equal(Enumerable.Range(11, 10).Reverse());

        var postsRequest = forum.Requests.Should().ContainSingle(r => r.Path == "/t/43/posts.json").Subject;
        postsRequest.ApiKey.Should().BeNull();
        postsRequest.Query.Should().Contain("post_ids[]=21").And.Contain("post_ids[]=30").And.NotContain("post_ids[]=20&");
    }

    [Fact]
    public async Task Get_TopicHiddenFromThePublicIsUnavailable()
    {
        var forum = new FakeForum { TopicId = 43, TopicStatus = HttpStatusCode.NotFound };

        (await forum.Service().GetAsync(EmbedUrl, null, CancellationToken.None)).Status.Should().Be(ForumDiscussionStatus.Unavailable);
    }

    [Fact]
    public async Task Get_ForumErrorIsUnavailable()
    {
        var forum = new FakeForum { InfoStatus = HttpStatusCode.TooManyRequests };

        (await forum.Service().GetAsync(EmbedUrl, null, CancellationToken.None)).Status.Should().Be(ForumDiscussionStatus.Unavailable);
    }

    private static object Post(int id, int number, int postType = 1, bool hidden = false, int? replyToNumber = null, string? replyToUser = null,
                               bool deletedAccount = false) => new
    {
        id,
        post_number = number,
        post_type = postType,
        username = deletedAccount ? null : $"u{id}",
        avatar_template = deletedAccount ? null : $"/user_avatar/community.jiten.moe/u{id}/{{size}}/1.png",
        created_at = "2026-10-02T13:04:59.749Z",
        cooked = $"<p>post {id}</p>",
        hidden,
        user_deleted = false,
        reply_to_post_number = replyToNumber,
        reply_to_user = replyToUser == null ? null : new { username = replyToUser }
    };

    private record SeenRequest(HttpMethod Method, string Path, string Query, string? ApiKey, string? ApiUsername, string? Body);

    private class FakeForum : HttpMessageHandler, IHttpClientFactory
    {
        public int? TopicId { get; set; }
        public HttpStatusCode CreateStatus { get; init; } = HttpStatusCode.OK;

        /// <summary>Simulates another instance creating the topic just before this one's creation is refused.</summary>
        public int? TopicIdAfterRefusal { get; init; }

        public HttpStatusCode InfoStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode TopicStatus { get; init; } = HttpStatusCode.OK;
        public int PostsInTopicResponse { get; init; } = 20;
        public TimeSpan Delay { get; init; }
        public List<object> Posts { get; } = [];
        public List<SeenRequest> Requests { get; } = [];

        public ForumDiscussionService Service(string apiKey = "key", int lookups = 15, int creations = 5, Dictionary<ForumSubject, int>? categories = null,
                                              string topicWriteKey = "") =>
            new(this, new MemoryCache(new MemoryCacheOptions()),
                Options.Create(new DiscourseOptions
                {
                    Url = Forum, ApiKey = apiKey, MediaHosts = [MediaHost], LookupsPerMinute = lookups, CreationsPerMinute = creations,
                    CategoryIds = categories ?? [], TopicWriteApiKey = topicWriteKey
                }),
                NullLogger<ForumDiscussionService>.Instance);

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri(Forum + "/") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            var uri = request.RequestUri!;
            var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add(new SeenRequest(request.Method, uri.AbsolutePath, Uri.UnescapeDataString(uri.Query),
                request.Headers.TryGetValues("Api-Key", out var key) ? key.Single() : null,
                request.Headers.TryGetValues("Api-Username", out var user) ? user.Single() : null, body));

            if (uri.AbsolutePath == "/posts.json")
            {
                if (CreateStatus != HttpStatusCode.OK)
                {
                    TopicId = TopicIdAfterRefusal;
                    return new HttpResponseMessage(CreateStatus);
                }
                TopicId = 43;
                return Json(new { id = 900, topic_id = TopicId, topic_slug = "a-topic" });
            }

            return uri.AbsolutePath switch
            {
                "/embed/info" when InfoStatus != HttpStatusCode.OK => new HttpResponseMessage(InfoStatus),
                "/embed/info" when TopicId == null => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/embed/info" => Json(new { topic_id = TopicId }),
                var p when p == $"/t/{TopicId}.json" && TopicStatus != HttpStatusCode.OK => new HttpResponseMessage(TopicStatus),
                var p when p == $"/t/{TopicId}.json" => Json(new
                {
                    id = TopicId,
                    slug = "a-topic",
                    posts_count = Posts.Count(post => (int)post.GetType().GetProperty("post_type")!.GetValue(post)! == 1),
                    post_stream = new { posts = Posts.Take(PostsInTopicResponse), stream = Posts.Select(IdOf) }
                }),
                var p when p == $"/t/{TopicId}/posts.json" => Json(new
                {
                    post_stream = new { posts = Posts.Where(post => uri.Query.Contains($"post_ids[]={IdOf(post)}")) }
                }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        }

        private static int IdOf(object post) => (int)post.GetType().GetProperty("id")!.GetValue(post)!;

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
