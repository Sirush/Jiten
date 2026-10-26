using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Jiten.Api.Services.Forum;

public interface IForumDiscussionService
{
    /// <summary>
    /// Threads of the forum topic embedded at <paramref name="embedUrl"/>, latest activity first, continuing after <paramref name="before"/>
    /// (a <see cref="ForumDiscussionDto.NextBefore"/>). Never creates a topic.
    /// </summary>
    Task<ForumDiscussionDto> GetAsync(string embedUrl, int? before, CancellationToken ct);

    /// <summary>Creates the topic for <paramref name="draft"/>, or returns it when it already exists.</summary>
    Task<ForumDiscussionDto> StartAsync(ForumTopicDraft draft, CancellationToken ct);

    /// <summary>Reads a known topic without the API key; lets development preview real threads for pages that don't exist on the live site.</summary>
    Task<ForumDiscussionDto> GetByTopicIdAsync(int topicId, int? before, CancellationToken ct);

    /// <summary>Re-reads a topic past its cache, for a forum webhook reporting a change to it.</summary>
    Task<ForumDiscussionDto> RefreshTopicAsync(int topicId, CancellationToken ct);
}

public class ForumDiscussionService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    IOptions<DiscourseOptions> options,
    ILogger<ForumDiscussionService> logger) : IForumDiscussionService
{
    public const string HttpClientName = "Discourse";
    private const int ThreadsPerPage = 10;

    /// <summary>
    /// Threading needs every post's parent, so a topic is read whole; past this many replies only the newest are threaded.
    /// Each 20 posts beyond the first costs one anonymous forum request per topic fill.
    /// </summary>
    private const int MaxThreadedPosts = 400;

    private const int PostsPerRequest = 20;

    /// <summary>After a failure or a 429 every forum call stops for this long, so retries from page views can't keep a block alive.</summary>
    private static readonly TimeSpan PauseAfterFailure = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan MaxPause = TimeSpan.FromMinutes(10);

    /// <summary>Keyed calls run as staff, which skips Discourse's own one-topic-per-minute creation throttle, so these budgets replace it.</summary>
    private readonly FixedWindowRateLimiter _lookups = Budget(options.Value.LookupsPerMinute, TimeSpan.FromMinutes(1));

    private readonly FixedWindowRateLimiter _creations = Budget(options.Value.CreationsPerMinute, TimeSpan.FromMinutes(1));
    private readonly FixedWindowRateLimiter _anonymousReads = Budget(options.Value.AnonymousReadsPer10Seconds, TimeSpan.FromSeconds(10));

    private readonly ForumPostSanitizer _sanitizer = new(options.Value.Url, options.Value.MediaHosts);

    /// <summary>One fill per topic at a time; concurrent views of a cold topic share it instead of each reading the whole topic.</summary>
    private readonly ConcurrentDictionary<int, Lazy<Task<ThreadedTopic?>>> _topicFills = new();

    /// <summary>Concurrent views of a cold page share one keyed lookup, so they spend one permit of the lookup budget.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<int?>>> _topicIdLookups = new();

    /// <summary>Concurrent starts of one page share a creation; a second would be refused, since the forum allows one topic per embed URL.</summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<int?>>> _topicCreations = new();

    private long _pausedUntilTicks;

    /// <summary>An embed URL keeps its topic for good, so a found id only expires to pick up a topic a moderator deleted.</summary>
    private static readonly TimeSpan TopicIdTtl = TimeSpan.FromHours(6);

    /// <summary>Topics only appear through <see cref="StartAsync"/>, which clears this, so a page without one can stay cached for a while.</summary>
    private static readonly TimeSpan NoTopicTtl = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan TopicTtl = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private string ForumUrl => options.Value.Url.TrimEnd('/');

    /// <summary>
    /// No redirects: HttpClient re-sends custom headers such as Api-Key to wherever one points. No cookies: anonymous reads
    /// must not pick up a forum session.
    /// </summary>
    public static HttpMessageHandler CreatePrimaryHandler() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };

    public Task<ForumDiscussionDto> GetAsync(string embedUrl, int? before, CancellationToken ct) =>
        GuardAsync(options.Value.IsDiscussionConfigured, embedUrl, ct, async () =>
        {
            var topicId = await FindTopicIdAsync(embedUrl, ct);
            return topicId != null ? await ReadTopicAsync(topicId.Value, before, ct) : ForumDiscussionDto.NotStarted;
        });

    public Task<ForumDiscussionDto> StartAsync(ForumTopicDraft draft, CancellationToken ct) =>
        GuardAsync(options.Value.IsDiscussionConfigured, draft.EmbedUrl, ct, async () =>
        {
            cache.Remove(TopicIdKey(draft.EmbedUrl));
            var topicId = await FindTopicIdAsync(draft.EmbedUrl, ct)
                          ?? await Coalesce(_topicCreations, draft.EmbedUrl, _ => CreateTopicAsync(draft)).WaitAsync(ct);
            return topicId != null ? await ReadTopicAsync(topicId.Value, null, ct) : ForumDiscussionDto.Unavailable;
        });

    /// <summary>
    /// Skips validation like the forum's own embed import, since deck titles repeat across media types and can be shorter than the forum's
    /// minimum. A refusal usually means another instance created the topic first, so the page is looked up again.
    /// </summary>
    private async Task<int?> CreateTopicAsync(ForumTopicDraft draft)
    {
        if (!TryTake(_creations, "creation")) throw new ForumBusyException();

        var categoryId = options.Value.CategoryIds.GetValueOrDefault(draft.Subject);
        var request = KeyedRequest(HttpMethod.Post, "posts.json", WriteApiKey);
        request.Content = JsonContent.Create(
            new CreateTopicBody(draft.Title, draft.Raw, draft.EmbedUrl, SkipValidations: true, categoryId > 0 ? categoryId : null), options: Json);
        using var response = await SendAsync(request, CancellationToken.None);

        if (response.IsSuccessStatusCode && await response.Content.ReadFromJsonAsync<CreatedPost>(Json) is { } created)
        {
            cache.Set(TopicIdKey(draft.EmbedUrl), (int?)created.TopicId, TopicIdTtl);
            return created.TopicId;
        }

        logger.LogWarning("Forum refused to create a topic for {EmbedUrl}: {Status}", draft.EmbedUrl, (int)response.StatusCode);
        cache.Remove(TopicIdKey(draft.EmbedUrl));
        return await LookUpTopicIdAsync(draft.EmbedUrl);
    }

    public Task<ForumDiscussionDto> GetByTopicIdAsync(int topicId, int? before, CancellationToken ct) =>
        GuardAsync(true, $"topic {topicId}", ct, () => ReadTopicAsync(topicId, before, ct));

    public Task<ForumDiscussionDto> RefreshTopicAsync(int topicId, CancellationToken ct)
    {
        cache.Remove(ThreadsKey(topicId));
        return GetByTopicIdAsync(topicId, null, ct);
    }

    /// <summary>Forum failures and spent budgets degrade to <see cref="ForumDiscussionDto.Unavailable"/>; only the caller's own cancellation propagates.</summary>
    private async Task<ForumDiscussionDto> GuardAsync(bool configured, string subject, CancellationToken ct, Func<Task<ForumDiscussionDto>> body)
    {
        if (!configured || IsPaused) return ForumDiscussionDto.Unavailable;

        try
        {
            return await body();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or ForumBusyException)
        {
            if (ct.IsCancellationRequested) throw;
            if (e is not ForumBusyException) logger.LogWarning(e, "Forum discussion unavailable for {Subject}", subject);
            return ForumDiscussionDto.Unavailable;
        }
    }

    private async Task<ForumDiscussionDto> ReadTopicAsync(int topicId, int? before, CancellationToken ct)
    {
        var topic = await GetThreadedTopicAsync(topicId, ct);
        if (topic == null) return ForumDiscussionDto.Unavailable;

        var (threads, nextBefore) = SelectThreadPage(topic.Threads, t => t.LastPostNumber, before, ThreadsPerPage);
        return new ForumDiscussionDto
        {
            Status = ForumDiscussionStatus.Ready,
            TopicId = topicId,
            TopicUrl = topic.TopicUrl,
            ReplyCount = topic.ReplyCount,
            Threads = threads.Select(t => t.Thread).ToList(),
            NextBefore = nextBefore
        };
    }

    /// <summary>
    /// Groups replies under the reply they answer, following chains up to the first post that answers the topic itself or a post
    /// that isn't listed (hidden, deleted, or past the threading cap). Each thread lists its root first, then the rest in posting order;
    /// threads come back latest activity first.
    /// </summary>
    public static List<List<T>> BuildThreads<T>(IReadOnlyList<T> posts, Func<T, int> number, Func<T, int?> replyTo)
    {
        var byNumber = posts.ToDictionary(number);

        int RootOf(T post)
        {
            var current = post;
            for (var hops = 0; hops < posts.Count; hops++)
            {
                var parent = replyTo(current);
                if (parent is null or 1 || !byNumber.TryGetValue(parent.Value, out var next)) break;
                current = next;
            }
            return number(current);
        }

        return posts.GroupBy(RootOf)
                    .Select(g => g.OrderBy(number).ToList())
                    .OrderByDescending(thread => number(thread[^1]))
                    .ToList();
    }

    /// <summary>The cursor is the latest post number of the last thread shown; threads with newer activity have already been served.</summary>
    public static (List<T> Page, int? NextBefore) SelectThreadPage<T>(IReadOnlyList<T> threads, Func<T, int> lastPostNumber, int? before, int pageSize)
    {
        var remaining = before == null ? threads.ToList() : threads.Where(t => lastPostNumber(t) < before).ToList();
        var page = remaining.Take(pageSize).ToList();
        return (page, remaining.Count > pageSize ? lastPostNumber(page[^1]) : null);
    }

    private async Task<int?> FindTopicIdAsync(string embedUrl, CancellationToken ct)
    {
        if (cache.TryGetValue(TopicIdKey(embedUrl), out int? cached)) return cached;
        return await Coalesce(_topicIdLookups, embedUrl, LookUpTopicIdAsync).WaitAsync(ct);
    }

    private async Task<int?> LookUpTopicIdAsync(string embedUrl)
    {
        var key = TopicIdKey(embedUrl);
        if (cache.TryGetValue(key, out int? cached)) return cached;
        if (!TryTake(_lookups, "lookup")) throw new ForumBusyException();

        using var response = await SendAsync(KeyedRequest(HttpMethod.Get, $"embed/info?embed_url={Uri.EscapeDataString(embedUrl)}", options.Value.ApiKey), CancellationToken.None);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            cache.Set(key, (int?)null, NoTopicTtl);
            return null;
        }

        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync<EmbedInfo>(Json);
        if (info == null) return null;

        cache.Set(key, (int?)info.TopicId, TopicIdTtl);
        return info.TopicId;
    }

    private static (string, string) TopicIdKey(string embedUrl) => ("forum-topic-id", embedUrl);

    private static (string, int) ThreadsKey(int topicId) => ("forum-threads", topicId);

    private async Task<ThreadedTopic?> GetThreadedTopicAsync(int topicId, CancellationToken ct)
    {
        if (cache.TryGetValue(ThreadsKey(topicId), out ThreadedTopic? cached)) return cached;
        return await Coalesce(_topicFills, topicId, FillTopicAsync).WaitAsync(ct);
    }

    /// <summary>
    /// Read anonymously, so a topic moved to a private category, and hidden or deleted posts, stay out of the site.
    /// Cached already sanitised, since every page of a topic reuses the same threads.
    /// </summary>
    private async Task<ThreadedTopic?> FillTopicAsync(int topicId)
    {
        if (cache.TryGetValue(ThreadsKey(topicId), out ThreadedTopic? cached)) return cached;

        using var response = await SendAnonymousAsync($"t/{topicId}.json", CancellationToken.None);
        ThreadedTopic? result = null;

        if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Forbidden))
        {
            response.EnsureSuccessStatusCode();
            var topic = await response.Content.ReadFromJsonAsync<TopicJson>(Json);
            if (topic != null) result = await BuildTopicAsync(topic);
        }

        cache.Set(ThreadsKey(topicId), result, TopicTtl);
        return result;
    }

    /// <summary>Callers for one key share a single run that ignores their cancellation; runs re-check the cache, since a caller can miss it just before an earlier run fills it.</summary>
    private static Task<T> Coalesce<TKey, T>(ConcurrentDictionary<TKey, Lazy<Task<T>>> inFlight, TKey key, Func<TKey, Task<T>> run)
        where TKey : notnull =>
        inFlight.GetOrAdd(key, k => new Lazy<Task<T>>(async () =>
        {
            try
            {
                return await run(k);
            }
            finally
            {
                inFlight.TryRemove(k, out _);
            }
        })).Value;

    private async Task<ThreadedTopic> BuildTopicAsync(TopicJson topic)
    {
        var replies = (await GetRepliesAsync(topic))
                      .Where(p => p is { PostType: RegularPostType, Hidden: false, UserDeleted: false, Username: not null, Cooked: not null })
                      .ToList();
        var topicUrl = $"{ForumUrl}/t/{topic.Slug}/{topic.Id}";
        var threads = BuildThreads(replies, p => p.PostNumber, p => p.ReplyToPostNumber)
                      .Select(thread => new CachedThread(ToThreadDto(thread, topicUrl), thread[^1].PostNumber))
                      .ToList();

        return new ThreadedTopic(topicUrl, Math.Max(0, topic.PostsCount - 1), threads);
    }

    /// <summary>The topic response carries its first posts; the newest replies beyond those are fetched in chunks.</summary>
    private async Task<List<PostJson>> GetRepliesAsync(TopicJson topic)
    {
        var replyIds = (topic.PostStream.Stream ?? []).Skip(1).TakeLast(MaxThreadedPosts).ToList();
        var posts = topic.PostStream.Posts.ToDictionary(p => p.Id);

        foreach (var chunk in replyIds.Where(id => !posts.ContainsKey(id)).Chunk(PostsPerRequest))
        {
            var query = string.Join('&', chunk.Select(id => $"post_ids[]={id}"));
            using var response = await SendAnonymousAsync($"t/{topic.Id}/posts.json?{query}", CancellationToken.None);
            response.EnsureSuccessStatusCode();
            var fetched = await response.Content.ReadFromJsonAsync<PostsJson>(Json);
            foreach (var post in fetched?.PostStream.Posts ?? [])
                posts[post.Id] = post;
        }

        return replyIds.Select(posts.GetValueOrDefault).OfType<PostJson>().ToList();
    }

    /// <summary>Replies straight to the root need no "replying to" label, the indent already says it.</summary>
    private ForumThreadDto ToThreadDto(List<PostJson> thread, string topicUrl)
    {
        var root = thread[0];
        return new ForumThreadDto
        {
            Post = ToDto(root, topicUrl, root.ReplyToPostNumber is null or 1 ? null : root.ReplyToUser?.Username),
            Replies = thread.Skip(1)
                            .Select(p => ToDto(p, topicUrl, p.ReplyToPostNumber == root.PostNumber ? null : p.ReplyToUser?.Username))
                            .ToList()
        };
    }

    private ForumPostDto ToDto(PostJson post, string topicUrl, string? replyToUsername) =>
        new()
        {
            PostId = post.Id,
            Username = post.Username!,
            AvatarUrl = AvatarUrl(post.AvatarTemplate),
            CreatedAt = post.CreatedAt,
            Html = _sanitizer.Sanitize(post.Cooked!),
            ReplyToUsername = replyToUsername,
            Url = $"{topicUrl}/{post.PostNumber}"
        };

    /// <summary>Templates are forum-relative or protocol-relative; 96px covers the 48px avatar at 2x.</summary>
    private string AvatarUrl(string? template)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var url = template.Replace("{size}", "96");
        if (url.StartsWith("//")) url = "https:" + url;
        else if (url.StartsWith('/')) url = ForumUrl + url;
        return url.StartsWith("https://") ? url : "";
    }

    private bool IsPaused => DateTime.UtcNow.Ticks < Volatile.Read(ref _pausedUntilTicks);

    private void Pause(TimeSpan? retryAfter, string reason)
    {
        var length = TimeSpan.FromTicks(Math.Clamp((retryAfter ?? PauseAfterFailure).Ticks, PauseAfterFailure.Ticks, MaxPause.Ticks));
        Volatile.Write(ref _pausedUntilTicks, (DateTime.UtcNow + length).Ticks);
        logger.LogWarning("Forum calls paused for {Seconds}s: {Reason}", (int)length.TotalSeconds, reason);
    }

    private Task<HttpResponseMessage> SendAnonymousAsync(string path, CancellationToken ct)
    {
        if (!TryTake(_anonymousReads, "anonymous read")) throw new ForumBusyException();
        return SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct);
    }

    /// <summary>A 429, a server error or a network failure pauses every forum call; the response is returned for the caller to judge otherwise.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            HttpResponseMessage response;
            try
            {
                response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct);
            }
            catch (HttpRequestException e)
            {
                Pause(null, e.Message);
                throw;
            }

            if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500) return response;

            Pause(RetryAfterHeader.Read(response), $"{(int)response.StatusCode} from {request.RequestUri?.AbsolutePath}");
            response.Dispose();
            throw new HttpRequestException($"Forum answered {(int)response.StatusCode}");
        }
    }

    private bool TryTake(RateLimiter budget, string kind)
    {
        using var lease = budget.AttemptAcquire();
        if (lease.IsAcquired) return true;

        var logKey = ("forum-budget-logged", kind);
        if (!cache.TryGetValue(logKey, out _))
        {
            cache.Set(logKey, true, TimeSpan.FromMinutes(1));
            logger.LogWarning("Forum {Kind} budget exhausted; those calls are skipped until it refills", kind);
        }
        return false;
    }

    private string WriteApiKey => string.IsNullOrWhiteSpace(options.Value.TopicWriteApiKey) ? options.Value.ApiKey : options.Value.TopicWriteApiKey;

    private HttpRequestMessage KeyedRequest(HttpMethod method, string path, string apiKey)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Api-Key", apiKey);
        request.Headers.Add("Api-Username", options.Value.ApiUsername);
        return request;
    }

    private static FixedWindowRateLimiter Budget(int permits, TimeSpan window) =>
        new(new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(1, permits), Window = window, QueueLimit = 0, AutoReplenishment = true });

    private const int RegularPostType = 1;

    private sealed class ForumBusyException : Exception;

    private record CachedThread(ForumThreadDto Thread, int LastPostNumber);

    private record ThreadedTopic(string TopicUrl, int ReplyCount, List<CachedThread> Threads);

    private record EmbedInfo(int TopicId);

    private record CreatedPost(int TopicId);

    private record CreateTopicBody(
        string Title,
        string Raw,
        string EmbedUrl,
        bool SkipValidations,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Category);

    /// <summary>PostsCount counts only regular posts, unlike the stream, which also lists moderator notices such as "pinned".</summary>
    private record TopicJson(int Id, string Slug, int PostsCount, TopicPostStream PostStream);

    private record TopicPostStream(List<PostJson> Posts, List<int>? Stream);

    private record PostsJson(TopicPostStream PostStream);

    private record ReplyToUser(string Username);

    /// <summary>Username and AvatarTemplate are null when the author's account was deleted.</summary>
    private record PostJson(
        int Id,
        int PostNumber,
        int PostType,
        string? Username,
        string? AvatarTemplate,
        DateTime CreatedAt,
        string? Cooked,
        bool Hidden,
        bool UserDeleted,
        int? ReplyToPostNumber,
        ReplyToUser? ReplyToUser);
}
