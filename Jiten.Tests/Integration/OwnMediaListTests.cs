using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class OwnMediaListTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedTrackedDeck(string title, MediaType mediaType, DeckStatus status, string userId,
                                            bool isFavourite = false, string coverName = "nocover.jpg")
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var deck = new Deck { OriginalTitle = title, MediaType = mediaType, UniqueWordCount = 100, CoverName = coverName };
        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();

        userDb.UserDeckPreferences.Add(new UserDeckPreference
                                      {
                                          UserId = userId, DeckId = deck.DeckId, Status = status, IsFavourite = isFavourite
                                      });
        await userDb.SaveChangesAsync();

        return deck.DeckId;
    }

    [Fact]
    public async Task ReturnsOwnEntries_WithStatusAndFavourite()
    {
        var completedId = await SeedTrackedDeck("Zebra Show", MediaType.Anime, DeckStatus.Completed, TestUsers.UserA, isFavourite: true,
                                                coverName: "https://cdn.test/zebra.jpg");
        var planningId = await SeedTrackedDeck("Alpha Novel", MediaType.Novel, DeckStatus.Planning, TestUsers.UserA);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/user/media-list").WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        items.Should().HaveCount(2);

        // Ordered by title, so the novel comes first.
        items![0].GetProperty("deckId").GetInt32().Should().Be(planningId);
        items[0].GetProperty("originalTitle").GetString().Should().Be("Alpha Novel");
        items[0].GetProperty("status").GetInt32().Should().Be((int)DeckStatus.Planning);
        items[0].GetProperty("mediaType").GetInt32().Should().Be((int)MediaType.Novel);
        items[0].GetProperty("isFavourite").GetBoolean().Should().BeFalse();

        items[1].GetProperty("deckId").GetInt32().Should().Be(completedId);
        items[1].GetProperty("status").GetInt32().Should().Be((int)DeckStatus.Completed);
        items[1].GetProperty("isFavourite").GetBoolean().Should().BeTrue();
        items[1].GetProperty("coverName").GetString().Should().Be("https://cdn.test/zebra.jpg");
    }

    [Fact]
    public async Task ExcludesOtherUsersEntries()
    {
        var mine = await SeedTrackedDeck("Mine", MediaType.Anime, DeckStatus.Ongoing, TestUsers.UserA);
        await SeedTrackedDeck("Theirs", MediaType.Anime, DeckStatus.Ongoing, TestUsers.UserB);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/user/media-list").WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        items.Should().HaveCount(1);
        items![0].GetProperty("deckId").GetInt32().Should().Be(mine);
    }

    [Fact]
    public async Task Anonymous_IsUnauthorized()
    {
        var response = await _client.GetAsync("/api/user/media-list");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
