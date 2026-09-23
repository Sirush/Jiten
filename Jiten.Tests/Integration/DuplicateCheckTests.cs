using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class DuplicateCheckTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedDeck(string title, MediaType mediaType = MediaType.VisualNovel, string? linkUrl = null,
        LinkType linkType = LinkType.Vndb, int? parentDeckId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var deck = new Deck { OriginalTitle = title, MediaType = mediaType, ParentDeckId = parentDeckId };
        deck.Titles.Add(new DeckTitle { Title = title, TitleType = DeckTitleType.Original });
        if (linkUrl != null)
            deck.Links.Add(new Link { Url = linkUrl, LinkType = linkType });
        db.Decks.Add(deck);
        await db.SaveChangesAsync();
        return deck.DeckId;
    }

    private async Task<int> SeedRequest(string title, MediaRequestStatus status = MediaRequestStatus.Open,
        MediaType mediaType = MediaType.VisualNovel, string? externalUrl = null, MediaRequestKind kind = MediaRequestKind.New,
        int? fulfilledDeckId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var request = new MediaRequest
        {
            Title = title,
            Status = status,
            MediaType = mediaType,
            ExternalUrl = externalUrl,
            Kind = kind,
            FulfilledDeckId = fulfilledDeckId,
            RequesterId = TestUsers.UserA
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private async Task<JsonElement> Get(string url, bool admin = false)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        message = admin ? message.WithAdmin() : message.WithUser(TestUsers.UserB);
        var response = await _client.SendAsync(message);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<(int Id, bool Exact)> Decks(JsonElement body) =>
        body.GetProperty("existingDecks").EnumerateArray()
            .Select(d => (d.GetProperty("deckId").GetInt32(), d.GetProperty("isExactMatch").GetBoolean()))
            .ToList();

    private static List<(int Id, bool Exact)> Requests(JsonElement body) =>
        body.GetProperty("existingRequests").EnumerateArray()
            .Select(r => (r.GetProperty("id").GetInt32(), r.GetProperty("isExactMatch").GetBoolean()))
            .ToList();

    [Fact]
    public async Task Title_FindsDeckAndActiveRequests_ButNotClosedOnes()
    {
        var deckId = await SeedDeck("Summer Pockets");
        var openId = await SeedRequest("Summer Pockets Reflection Blue");
        var inProgressId = await SeedRequest("summer pockets", MediaRequestStatus.InProgress);
        await SeedRequest("Summer Pockets rejected", MediaRequestStatus.Rejected);
        await SeedRequest("Summer Pockets done", MediaRequestStatus.Completed);

        var body = await Get("/api/requests/duplicate-check?title=Summer%20Pockets");

        Decks(body).Should().BeEquivalentTo([(deckId, true)]);
        Requests(body).Should().BeEquivalentTo([(inProgressId, true), (openId, false)]);
        Requests(body)[0].Id.Should().Be(inProgressId);
    }

    [Fact]
    public async Task ExternalUrl_MatchesSameEntityUnderUnrelatedTitle()
    {
        var deckId = await SeedDeck("サマーポケッツ", linkUrl: "https://vndb.org/v20424");
        await SeedDeck("Other game", linkUrl: "https://vndb.org/v204240");
        var requestId = await SeedRequest("Some request", externalUrl: "https://vndb.org/v20424/releases");

        var body = await Get("/api/requests/duplicate-check?title=Completely%20different&externalUrl="
                             + Uri.EscapeDataString("https://www.vndb.org/v20424/"));

        Decks(body).Should().BeEquivalentTo([(deckId, true)]);
        Requests(body).Should().BeEquivalentTo([(requestId, true)]);
    }

    [Fact]
    public async Task MediaType_KeepsOnlyMatchesOfThatType()
    {
        await SeedDeck("Steins;Gate", MediaType.VisualNovel);
        var animeDeckId = await SeedDeck("Steins;Gate", MediaType.Anime);
        await SeedRequest("Steins;Gate", mediaType: MediaType.VisualNovel);
        var animeRequestId = await SeedRequest("Steins;Gate", mediaType: MediaType.Anime);

        var filtered = await Get($"/api/requests/duplicate-check?title=Steins%3BGate&mediaType={(int)MediaType.Anime}");
        Decks(filtered).Should().BeEquivalentTo([(animeDeckId, true)]);
        Requests(filtered).Should().BeEquivalentTo([(animeRequestId, true)]);

        var unfiltered = await Get("/api/requests/duplicate-check?title=Steins%3BGate");
        Decks(unfiltered).Should().HaveCount(2);
        Requests(unfiltered).Should().HaveCount(2);
    }

    [Fact]
    public async Task ExternalUrl_UnsupportedLink_ChangesNothing()
    {
        await SeedRequest("Web novel", mediaType: MediaType.WebNovel, externalUrl: "https://ncode.syosetu.com/n1234ab/");

        var body = await Get("/api/requests/duplicate-check?externalUrl=" + Uri.EscapeDataString("https://ncode.syosetu.com/n1234ab/"));

        Decks(body).Should().BeEmpty();
        Requests(body).Should().BeEmpty();
    }

    [Fact]
    public async Task ShortTitle_WithoutUrl_ReturnsEmptyLists()
    {
        await SeedDeck("A");

        var body = await Get("/api/requests/duplicate-check?title=A");

        Decks(body).Should().BeEmpty();
        Requests(body).Should().BeEmpty();
        body.GetProperty("existingUpdateRequests").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Title_IgnoresSubdecks()
    {
        var parentId = await SeedDeck("Parent Series");
        await SeedDeck("Parent Series Volume 1", parentDeckId: parentId);

        var body = await Get("/api/requests/duplicate-check?title=Parent%20Series");

        Decks(body).Select(d => d.Id).Should().BeEquivalentTo([parentId]);
    }

    [Fact]
    public async Task Admin_ReturnsFulfillableRequests_FilteredByMediaType()
    {
        var matching = await SeedRequest("Clannad", mediaType: MediaType.VisualNovel);
        var second = await SeedRequest("Clannad side stories", mediaType: MediaType.VisualNovel);
        await SeedRequest("Clannad", mediaType: MediaType.Anime);
        var deckId = await SeedDeck("Unrelated");
        await SeedRequest("Clannad", mediaType: MediaType.VisualNovel, kind: MediaRequestKind.Update);
        await SeedRequest("Clannad", MediaRequestStatus.InProgress, fulfilledDeckId: deckId);

        var body = await Get($"/api/admin/duplicate-check?title=Clannad&mediaType={(int)MediaType.VisualNovel}", admin: true);

        Requests(body).Should().BeEquivalentTo([(matching, true), (second, false)]);
    }

    [Fact]
    public async Task Admin_MatchesRequestsOnPostedLinks()
    {
        var requestId = await SeedRequest("A title nobody would type", externalUrl: "https://anilist.co/manga/30002/Berserk");
        await SeedRequest("Anime entry with the same number", externalUrl: "https://anilist.co/anime/30002");

        var body = await Get("/api/admin/duplicate-check?title=Nothing%20alike"
                             + "&links=" + Uri.EscapeDataString("https://vndb.org/v1")
                             + "&links=" + Uri.EscapeDataString("https://anilist.co/manga/30002"), admin: true);

        Requests(body).Should().BeEquivalentTo([(requestId, true)]);
    }

    [Fact]
    public async Task Admin_RejectsNonAdmin()
    {
        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/admin/duplicate-check?title=Clannad").WithUser(TestUsers.UserA));

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized);
    }
}
