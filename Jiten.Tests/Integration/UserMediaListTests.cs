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

public class UserMediaListTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedDeckWithStatus(string title, DeckStatus status, string userId = TestUsers.UserA)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var deck = new Deck { OriginalTitle = title, MediaType = MediaType.Anime, UniqueWordCount = 100 };
        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();

        userDb.UserDeckPreferences.Add(new UserDeckPreference { UserId = userId, DeckId = deck.DeckId, Status = status });
        await userDb.SaveChangesAsync();

        return deck.DeckId;
    }

    [Fact]
    public async Task OwnProfile_ReturnsMediaList_EvenWhenPrivate()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: false, isMediaListPublic: false);
        await SeedDeckWithStatus("Ongoing Show", DeckStatus.Ongoing);
        await SeedDeckWithStatus("Done Show", DeckStatus.Completed);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{TestUsers.UserA}/media-list")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task PublicMediaList_VisibleToOtherUsers()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: true);
        await SeedDeckWithStatus("Done Show", DeckStatus.Completed);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{TestUsers.UserA}/media-list")
            .WithUser(TestUsers.UserB);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        items.Should().HaveCount(1);
        items![0].GetProperty("status").GetInt32().Should().Be((int)DeckStatus.Completed);
        items[0].GetProperty("originalTitle").GetString().Should().Be("Done Show");
    }

    [Fact]
    public async Task PublicMediaList_HidesFavouritesFromOtherUsers()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: true);
        var deckId = await SeedDeckWithStatus("Loved Show", DeckStatus.Completed);
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var pref = await userDb.UserDeckPreferences.FirstAsync(p => p.UserId == TestUsers.UserA && p.DeckId == deckId);
            pref.IsFavourite = true;
            await userDb.SaveChangesAsync();
        }

        async Task<bool> FavouriteAs(string viewer)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{TestUsers.UserA}/media-list").WithUser(viewer);
            var items = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<List<JsonElement>>();
            return items![0].GetProperty("isFavourite").GetBoolean();
        }

        (await FavouriteAs(TestUsers.UserB)).Should().BeFalse();
        (await FavouriteAs(TestUsers.UserA)).Should().BeTrue();
    }

    [Fact]
    public async Task MediaListFlagOff_NotVisibleToOtherUsers()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: false);
        await SeedDeckWithStatus("Secret Show", DeckStatus.Completed);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{TestUsers.UserA}/media-list")
            .WithUser(TestUsers.UserB);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PrivateProfile_NotVisibleEvenIfMediaListFlagSet()
    {
        // Invariant-violating row: media list flagged public but profile private. Access must still be denied.
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: false, isMediaListPublic: true);
        await SeedDeckWithStatus("Secret Show", DeckStatus.Completed);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{TestUsers.UserA}/media-list")
            .WithUser(TestUsers.UserB);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ChildDecks_CollapseToParentSeries()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: false, isMediaListPublic: false);

        int parentId;
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

            var parent = new Deck { OriginalTitle = "Cowboy Bebop", MediaType = MediaType.Anime, UniqueWordCount = 500 };
            jitenDb.Decks.Add(parent);
            await jitenDb.SaveChangesAsync();
            parentId = parent.DeckId;

            var c1 = new Deck { OriginalTitle = "Episode 1", MediaType = MediaType.Anime, ParentDeckId = parentId, DeckOrder = 0 };
            var c2 = new Deck { OriginalTitle = "Episode 2", MediaType = MediaType.Anime, ParentDeckId = parentId, DeckOrder = 1 };
            jitenDb.Decks.AddRange(c1, c2);
            await jitenDb.SaveChangesAsync();

            userDb.UserDeckPreferences.Add(new UserDeckPreference { UserId = TestUsers.UserA, DeckId = c1.DeckId, Status = DeckStatus.Completed });
            userDb.UserDeckPreferences.Add(new UserDeckPreference { UserId = TestUsers.UserA, DeckId = c2.DeckId, Status = DeckStatus.Completed });
            await userDb.SaveChangesAsync();
        }

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{TestUsers.UserA}/media-list")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        items.Should().HaveCount(1);
        items![0].GetProperty("deckId").GetInt32().Should().Be(parentId);
        items[0].GetProperty("originalTitle").GetString().Should().Be("Cowboy Bebop");
        items[0].GetProperty("status").GetInt32().Should().Be((int)DeckStatus.Completed);
    }

    [Fact]
    public async Task UpdateProfile_PrivateProfileMakesTheLibraryPrivate()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);

        async Task<JsonElement> Patch(object privacy)
        {
            var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/api/user/profile")
                                                   .WithUser(TestUsers.UserA)
                                                   .WithJsonContent(new { privacy }));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var open = await Patch(new { profile = "public", sections = new Dictionary<string, string> { ["library"] = "everyone" }, hiddenFields = Array.Empty<string>(), hideAdultTitles = true });
        open.GetProperty("sections").EnumerateArray().Select(s => s.GetString()).Should().Contain("library");
        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{TestUsers.UserA}/media-list").WithUser(TestUsers.UserB)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        await Patch(new { profile = "private", sections = new Dictionary<string, string> { ["library"] = "everyone" }, hiddenFields = Array.Empty<string>(), hideAdultTitles = true });
        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{TestUsers.UserA}/media-list").WithUser(TestUsers.UserB)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var profile = await scope.ServiceProvider.GetRequiredService<UserDbContext>().UserProfiles.AsNoTracking()
                                 .FirstAsync(p => p.UserId == TestUsers.UserA);
        profile.IsPublic.Should().BeFalse();
        profile.IsMediaListPublic.Should().BeFalse();
    }
}
