using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class RequestKindTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedDeck(string title = "Existing Media", MediaType mediaType = MediaType.VisualNovel)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var deck = new Deck { OriginalTitle = title, MediaType = mediaType };
        db.Decks.Add(deck);
        await db.SaveChangesAsync();
        return deck.DeckId;
    }

    private Task<HttpResponseMessage> PostRequest(object payload, string userId = TestUsers.UserA) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/requests")
            .WithUser(userId)
            .WithJsonContent(payload));

    private async Task<int> CreateNewRequest(string title = "Brand New Media", string userId = TestUsers.UserA)
    {
        var response = await PostRequest(new { title, mediaType = (int)MediaType.Anime }, userId);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    private async Task<int> CreateUpdateRequest(int deckId, string? title = "Update Me", string userId = TestUsers.UserA)
    {
        var response = await PostRequest(new
        {
            title,
            mediaType = (int)MediaType.Anime,
            kind = (int)MediaRequestKind.Update,
            targetDeckId = deckId
        }, userId);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> GetRequest(int id, string userId = TestUsers.UserA)
    {
        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/requests/{id}").WithUser(userId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Create_WithoutKind_DefaultsToNewWithNoTarget()
    {
        var id = await CreateNewRequest();

        var dto = await GetRequest(id);
        dto.GetProperty("kind").GetInt32().Should().Be((int)MediaRequestKind.New);
        dto.GetProperty("targetDeckId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Create_Update_WithoutTargetDeck_Returns400()
    {
        var response = await PostRequest(new
        {
            title = "Missing target",
            mediaType = (int)MediaType.Anime,
            kind = (int)MediaRequestKind.Update
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_New_WithTargetDeck_Returns400()
    {
        var deckId = await SeedDeck();

        var response = await PostRequest(new
        {
            title = "New but targeted",
            mediaType = (int)MediaType.Anime,
            kind = (int)MediaRequestKind.New,
            targetDeckId = deckId
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_Update_WithUnknownDeck_Returns400()
    {
        var response = await PostRequest(new
        {
            title = "Ghost deck",
            mediaType = (int)MediaType.Anime,
            kind = (int)MediaRequestKind.Update,
            targetDeckId = 999_999
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_Update_TakesMediaTypeFromTargetDeck()
    {
        var deckId = await SeedDeck(mediaType: MediaType.VisualNovel);

        // Posted media type is Anime, the deck is a visual novel: the deck wins.
        var id = await CreateUpdateRequest(deckId);

        var dto = await GetRequest(id);
        dto.GetProperty("kind").GetInt32().Should().Be((int)MediaRequestKind.Update);
        dto.GetProperty("mediaType").GetInt32().Should().Be((int)MediaType.VisualNovel);
        dto.GetProperty("targetDeckId").GetInt32().Should().Be(deckId);
        dto.GetProperty("targetDeckTitle").GetString().Should().Be("Existing Media");
    }

    [Fact]
    public async Task Create_Update_WithoutTitle_UsesTargetDeckTitle()
    {
        var deckId = await SeedDeck("Steins;Gate");

        var id = await CreateUpdateRequest(deckId, title: null);

        var dto = await GetRequest(id);
        dto.GetProperty("title").GetString().Should().Be("Steins;Gate");
    }

    [Fact]
    public async Task Create_New_WithoutTitle_Returns400()
    {
        var response = await PostRequest(new { mediaType = (int)MediaType.Anime });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_Update_SecondActiveForSameDeck_SameUser_Returns409()
    {
        var deckId = await SeedDeck();
        await CreateUpdateRequest(deckId);

        var response = await PostRequest(new
        {
            title = "Same deck again",
            mediaType = (int)MediaType.Anime,
            kind = (int)MediaRequestKind.Update,
            targetDeckId = deckId
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_Update_SecondActiveForSameDeck_OtherUser_Succeeds()
    {
        var deckId = await SeedDeck();
        await CreateUpdateRequest(deckId, userId: TestUsers.UserA);

        var response = await PostRequest(new
        {
            title = "Same deck, other user",
            mediaType = (int)MediaType.Anime,
            kind = (int)MediaRequestKind.Update,
            targetDeckId = deckId
        }, TestUsers.UserB);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task List_FiltersByKind()
    {
        var deckId = await SeedDeck();
        var newId = await CreateNewRequest();
        var updateId = await CreateUpdateRequest(deckId);

        var updates = await ListRequests($"kind={(int)MediaRequestKind.Update}");
        updates.Should().BeEquivalentTo([updateId]);

        var news = await ListRequests($"kind={(int)MediaRequestKind.New}");
        news.Should().BeEquivalentTo([newId]);

        var all = await ListRequests(null);
        all.Should().BeEquivalentTo([newId, updateId]);
    }

    private async Task<List<int>> ListRequests(string? query)
    {
        var url = "/api/requests?status=" + (int)MediaRequestStatus.Open;
        if (query != null) url += "&" + query;

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").EnumerateArray().Select(r => r.GetProperty("id").GetInt32()).ToList();
    }

    [Fact]
    public async Task Facets_CountKinds_AndIgnoreOwnSelection()
    {
        var deckId = await SeedDeck();
        await CreateNewRequest();
        await CreateUpdateRequest(deckId);

        // Selecting a kind must not zero out the other kind's count.
        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/requests/facets?kind={(int)MediaRequestKind.Update}")
                .WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var kinds = body.GetProperty("kinds");
        kinds.GetProperty(((int)MediaRequestKind.New).ToString()).GetInt32().Should().Be(1);
        kinds.GetProperty(((int)MediaRequestKind.Update).ToString()).GetInt32().Should().Be(1);
        body.GetProperty("kindTotal").GetInt32().Should().Be(2);

        // Other dimensions are scoped by the active kind.
        body.GetProperty("statusTotal").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task DuplicateCheck_SurfacesActiveUpdateRequestsForDeck()
    {
        var deckId = await SeedDeck();
        var updateId = await CreateUpdateRequest(deckId);

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/requests/duplicate-check?targetDeckId={deckId}")
                .WithUser(TestUsers.UserB));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var existing = body.GetProperty("existingUpdateRequests").EnumerateArray().ToList();
        existing.Should().HaveCount(1);
        existing[0].GetProperty("id").GetInt32().Should().Be(updateId);
    }

    [Fact]
    public async Task AdminEdit_CanSwitchKindInBothDirections()
    {
        var deckId = await SeedDeck(mediaType: MediaType.VisualNovel);
        var id = await CreateNewRequest();

        var toUpdate = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/edit")
            .WithAdmin()
            .WithJsonContent(new
            {
                title = "Now an update",
                mediaType = (int)MediaType.Anime,
                kind = (int)MediaRequestKind.Update,
                targetDeckId = deckId
            }));
        toUpdate.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterUpdate = await GetRequest(id);
        afterUpdate.GetProperty("kind").GetInt32().Should().Be((int)MediaRequestKind.Update);
        afterUpdate.GetProperty("targetDeckId").GetInt32().Should().Be(deckId);
        afterUpdate.GetProperty("mediaType").GetInt32().Should().Be((int)MediaType.VisualNovel);

        var backToNew = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/edit")
            .WithAdmin()
            .WithJsonContent(new
            {
                title = "New again",
                mediaType = (int)MediaType.Anime,
                kind = (int)MediaRequestKind.New
            }));
        backToNew.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterRevert = await GetRequest(id);
        afterRevert.GetProperty("kind").GetInt32().Should().Be((int)MediaRequestKind.New);
        afterRevert.GetProperty("targetDeckId").ValueKind.Should().Be(JsonValueKind.Null);
        afterRevert.GetProperty("mediaType").GetInt32().Should().Be((int)MediaType.Anime);
    }

    [Fact]
    public async Task AdminEdit_ToUpdate_WithoutTargetDeck_Returns400()
    {
        var id = await CreateNewRequest();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/edit")
            .WithAdmin()
            .WithJsonContent(new
            {
                title = "No target",
                mediaType = (int)MediaType.Anime,
                kind = (int)MediaRequestKind.Update
            }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Requester_CanRetargetOwnUpdateRequest()
    {
        var firstDeckId = await SeedDeck("First Deck", MediaType.Anime);
        var secondDeckId = await SeedDeck("Second Deck", MediaType.Manga);
        var id = await CreateUpdateRequest(firstDeckId);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/edit-description")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { description = "Actually this one", targetDeckId = secondDeckId }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await GetRequest(id);
        dto.GetProperty("targetDeckId").GetInt32().Should().Be(secondDeckId);
        dto.GetProperty("targetDeckTitle").GetString().Should().Be("Second Deck");
        dto.GetProperty("mediaType").GetInt32().Should().Be((int)MediaType.Manga);
    }

    [Fact]
    public async Task Requester_EditWithoutTarget_LeavesTargetUntouched()
    {
        var deckId = await SeedDeck();
        var id = await CreateUpdateRequest(deckId);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/edit-description")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { description = "Just a description edit" }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await GetRequest(id);
        dto.GetProperty("targetDeckId").GetInt32().Should().Be(deckId);
    }

    [Fact]
    public async Task Requester_RetargetOntoOwnOtherActiveRequest_Returns409()
    {
        var firstDeckId = await SeedDeck("First Deck");
        var secondDeckId = await SeedDeck("Second Deck");
        var id = await CreateUpdateRequest(firstDeckId);
        await CreateUpdateRequest(secondDeckId, title: "Other request");

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/edit-description")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { targetDeckId = secondDeckId }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Requester_CannotTargetMediaOnANewRequest()
    {
        var deckId = await SeedDeck();
        var id = await CreateNewRequest();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/edit-description")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { targetDeckId = deckId }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeletingTargetDeck_KeepsRequest_WithNullTarget()
    {
        var deckId = await SeedDeck();
        var id = await CreateUpdateRequest(deckId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var deck = await db.Decks.FirstAsync(d => d.DeckId == deckId);
            db.Decks.Remove(deck);
            await db.SaveChangesAsync();
        }

        var dto = await GetRequest(id);
        dto.GetProperty("kind").GetInt32().Should().Be((int)MediaRequestKind.Update);
        dto.GetProperty("targetDeckId").ValueKind.Should().Be(JsonValueKind.Null);
        dto.GetProperty("targetDeckTitle").ValueKind.Should().Be(JsonValueKind.Null);

        var listed = await ListRequests(null);
        listed.Should().Contain(id);
    }
}
