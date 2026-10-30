using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class SharedShelfTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedDeck(string title, int? parentDeckId = null)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var deck = new Deck { OriginalTitle = title, MediaType = MediaType.Novel, UniqueWordCount = 100, ParentDeckId = parentDeckId };
        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();
        return deck.DeckId;
    }

    private async Task SetStatus(string userId, int deckId, DeckStatus status, bool isFavourite = false)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserDeckPreferences.Add(new UserDeckPreference { UserId = userId, DeckId = deckId, Status = status, IsFavourite = isFavourite });
        await userDb.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> GetShelf(string? asUser, string target = TestUsers.UserA)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{target}/shared-shelf");
        if (asUser != null) request.WithUser(asUser);
        return _client.SendAsync(request);
    }

    private static List<string> Titles(JsonElement shelf, string bucket) =>
        shelf.GetProperty(bucket).EnumerateArray().Select(i => i.GetProperty("deck").GetProperty("originalTitle").GetString()!).ToList();

    [Fact]
    public async Task SplitsTitlesIntoSharedAndTheirsOnly()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: true);
        var both = await SeedDeck("Both Read");
        var planned = await SeedDeck("Viewer Planning");
        var unseen = await SeedDeck("Viewer Unseen");
        var droppedByViewer = await SeedDeck("Viewer Dropped");
        var theirPlanning = await SeedDeck("Owner Planning");
        var theirDropped = await SeedDeck("Owner Dropped");

        await SetStatus(TestUsers.UserA, both, DeckStatus.Completed, isFavourite: true);
        await SetStatus(TestUsers.UserA, planned, DeckStatus.Ongoing);
        await SetStatus(TestUsers.UserA, unseen, DeckStatus.Paused);
        await SetStatus(TestUsers.UserA, droppedByViewer, DeckStatus.Completed);
        await SetStatus(TestUsers.UserA, theirPlanning, DeckStatus.Planning);
        await SetStatus(TestUsers.UserA, theirDropped, DeckStatus.Dropped);

        await SetStatus(TestUsers.UserB, both, DeckStatus.Ongoing);
        await SetStatus(TestUsers.UserB, planned, DeckStatus.Planning);
        await SetStatus(TestUsers.UserB, droppedByViewer, DeckStatus.Dropped);
        await SetStatus(TestUsers.UserB, theirPlanning, DeckStatus.Completed);

        var response = await GetShelf(TestUsers.UserB);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var shelf = await response.Content.ReadFromJsonAsync<JsonElement>();
        shelf.GetProperty("viewerHasList").GetBoolean().Should().BeTrue();
        Titles(shelf, "shared").Should().Equal("Both Read");
        Titles(shelf, "theirsOnly").Should().BeEquivalentTo("Viewer Planning", "Viewer Unseen");
        shelf.GetProperty("sharedCount").GetInt32().Should().Be(1);
        shelf.GetProperty("theirsOnlyCount").GetInt32().Should().Be(2);

        var sharedItem = shelf.GetProperty("shared")[0];
        sharedItem.TryGetProperty("theirFavourite", out _).Should().BeFalse();
        sharedItem.GetProperty("deck").TryGetProperty("isFavourite", out _).Should().BeFalse();
        sharedItem.GetProperty("yourStatus").GetInt32().Should().Be((int)DeckStatus.Ongoing);

        var plannedItem = shelf.GetProperty("theirsOnly").EnumerateArray()
                               .First(i => i.GetProperty("deck").GetProperty("originalTitle").GetString() == "Viewer Planning");
        plannedItem.GetProperty("yourStatus").GetInt32().Should().Be((int)DeckStatus.Planning);
    }

    [Fact]
    public async Task ChildDeckStatuses_FoldToTheParentOnBothSides()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: true);
        var series = await SeedDeck("Series");
        var volume1 = await SeedDeck("Volume 1", series);
        var volume2 = await SeedDeck("Volume 2", series);

        await SetStatus(TestUsers.UserA, volume1, DeckStatus.Completed);
        await SetStatus(TestUsers.UserB, volume2, DeckStatus.Ongoing);

        var shelf = await (await GetShelf(TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();

        Titles(shelf, "shared").Should().Equal("Series");
        Titles(shelf, "theirsOnly").Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyViewerList_GetsEverythingTheyRead()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: true);
        await SetStatus(TestUsers.UserA, await SeedDeck("Read One"), DeckStatus.Completed);

        var shelf = await (await GetShelf(TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();

        shelf.GetProperty("viewerHasList").GetBoolean().Should().BeFalse();
        Titles(shelf, "theirsOnly").Should().Equal("Read One");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PrivateMediaList_Returns404(bool isPublic, bool isMediaListPublic)
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic, isMediaListPublic);
        await SetStatus(TestUsers.UserA, await SeedDeck("Secret"), DeckStatus.Completed);

        var response = await GetShelf(TestUsers.UserB);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OwnProfile_Returns400()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: true);

        var response = await GetShelf(TestUsers.UserA);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Anonymous_Returns401()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: true);

        var response = await GetShelf(null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
