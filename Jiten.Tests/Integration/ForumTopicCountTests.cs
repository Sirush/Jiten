using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services.Forum;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ForumTopicCountTests(JitenWebApplicationFactory factory) : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();
    private StubForumDiscussionService Forum => (StubForumDiscussionService)factory.Services.GetRequiredService<IForumDiscussionService>();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        Forum.Topics.Clear();
        Forum.RefreshedReplyCount = 0;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedDeck()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var deck = new Deck { OriginalTitle = $"Deck {Guid.NewGuid():N}", MediaType = MediaType.Anime };
        db.Decks.Add(deck);
        await db.SaveChangesAsync();
        return deck.DeckId;
    }

    private async Task<List<ForumTopic>> StoredTopics()
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<JitenDbContext>().ForumTopics.AsNoTracking().ToListAsync();
    }

    private async Task<int> DeckReplyCount(int deckId)
    {
        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/media-deck/{deckId}/detail");
        return detail.GetProperty("data").GetProperty("mainDeck").GetProperty("discussionReplyCount").GetInt32();
    }

    private async Task<int> ReadDiscussion(int deckId, int topicId, int replies)
    {
        Forum.Topics[$"https://jiten.moe/decks/media/{deckId}/detail"] = (topicId, replies);
        var response = await _client.GetAsync($"/api/forum/discussions/deck/{deckId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return deckId;
    }

    private Task<HttpResponseMessage> Webhook(string eventName, object payload, string? secret = JitenWebApplicationFactory.DiscourseWebhookSecret)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/forum/webhook") { Content = new ByteArrayContent(body) };
        request.Headers.Add("X-Discourse-Event", eventName);
        if (secret != null)
            request.Headers.Add("X-Discourse-Event-Signature", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)));
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task DiscussionRead_StoresTheReplyCountShownOnTheDeck()
    {
        var deckId = await ReadDiscussion(await SeedDeck(), topicId: 70, replies: 4);

        (await StoredTopics()).Should().ContainSingle(t => t.Subject == ForumSubject.Deck && t.SubjectId == deckId && t.TopicId == 70 && t.ReplyCount == 4);
        (await DeckReplyCount(deckId)).Should().Be(4);

        await ReadDiscussion(deckId, topicId: 70, replies: 5);
        (await StoredTopics()).Should().ContainSingle().Which.ReplyCount.Should().Be(5);
    }

    [Fact]
    public async Task DeckWithoutATopicShowsNoReplies()
    {
        (await DeckReplyCount(await SeedDeck())).Should().Be(0);
    }

    [Fact]
    public async Task Webhook_RejectsAMissingOrWrongSignature()
    {
        (await Webhook("post_created", new { post = new { topic_id = 70 } }, secret: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Webhook("post_created", new { post = new { topic_id = 70 } }, secret: "wrong")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_PostEventRefreshesTheStoredCount()
    {
        var deckId = await ReadDiscussion(await SeedDeck(), topicId: 70, replies: 4);
        Forum.RefreshedReplyCount = 6;

        (await Webhook("post_created", new { post = new { id = 900, topic_id = 70 } })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await DeckReplyCount(deckId)).Should().Be(6);
    }

    [Fact]
    public async Task Webhook_IgnoresTopicsItDoesNotKnow()
    {
        Forum.RefreshedReplyCount = 6;

        (await Webhook("post_created", new { post = new { topic_id = 71 } })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Webhook("ping", new { ping = "OK" })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await StoredTopics()).Should().BeEmpty();
    }

    [Fact]
    public async Task Webhook_DestroyedTopicDropsTheCount()
    {
        var deckId = await ReadDiscussion(await SeedDeck(), topicId: 70, replies: 4);

        await Webhook("topic_destroyed", new { topic = new { id = 70 } });

        (await StoredTopics()).Should().BeEmpty();
        (await DeckReplyCount(deckId)).Should().Be(0);
    }

    [Fact]
    public void TopicId_ReadsPostAndTopicPayloads()
    {
        DiscourseWebhook.TopicId("""{"post":{"id":1,"topic_id":42}}"""u8.ToArray()).Should().Be(42);
        DiscourseWebhook.TopicId("""{"topic":{"id":43}}"""u8.ToArray()).Should().Be(43);
        DiscourseWebhook.TopicId("""{"ping":"OK"}"""u8.ToArray()).Should().BeNull();
        DiscourseWebhook.TopicId("not json"u8.ToArray()).Should().BeNull();
    }
}
