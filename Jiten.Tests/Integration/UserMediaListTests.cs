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

    // Test users are seeded with UserName = guid but no NormalizedUserName; the endpoint resolves by
    // NormalizedUserName, so set it here along with both privacy flags (written directly so we can also
    // exercise the access guard against an invariant-violating row).
    private async Task ConfigureProfile(string userId, bool isPublic, bool isMediaListPublic)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var user = await userDb.Users.FirstAsync(u => u.Id == userId);
        user.NormalizedUserName = (user.UserName ?? userId).ToUpperInvariant();

        var profile = await userDb.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
        if (profile == null)
        {
            profile = new UserProfile { UserId = userId };
            userDb.UserProfiles.Add(profile);
        }
        profile.IsPublic = isPublic;
        profile.IsMediaListPublic = isMediaListPublic;

        await userDb.SaveChangesAsync();
    }

    [Fact]
    public async Task OwnProfile_ReturnsMediaList_EvenWhenPrivate()
    {
        await ConfigureProfile(TestUsers.UserA, isPublic: false, isMediaListPublic: false);
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
        await ConfigureProfile(TestUsers.UserA, isPublic: true, isMediaListPublic: true);
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
    public async Task MediaListFlagOff_NotVisibleToOtherUsers()
    {
        await ConfigureProfile(TestUsers.UserA, isPublic: true, isMediaListPublic: false);
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
        await ConfigureProfile(TestUsers.UserA, isPublic: false, isMediaListPublic: true);
        await SeedDeckWithStatus("Secret Show", DeckStatus.Completed);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{TestUsers.UserA}/media-list")
            .WithUser(TestUsers.UserB);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ChildDecks_CollapseToParentSeries()
    {
        await ConfigureProfile(TestUsers.UserA, isPublic: false, isMediaListPublic: false);

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
    public async Task UpdateProfile_MediaListPublicRequiresPublicProfile()
    {
        // Make both public.
        var patch1 = new HttpRequestMessage(HttpMethod.Patch, "/api/user/profile")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { isPublic = true, isMediaListPublic = true });
        (await _client.SendAsync(patch1)).StatusCode.Should().Be(HttpStatusCode.OK);

        var get1 = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/user/profile").WithUser(TestUsers.UserA));
        var body1 = await get1.Content.ReadFromJsonAsync<JsonElement>();
        body1.GetProperty("isPublic").GetBoolean().Should().BeTrue();
        body1.GetProperty("isMediaListPublic").GetBoolean().Should().BeTrue();

        // Turning the profile private auto-deactivates the media list flag.
        var patch2 = new HttpRequestMessage(HttpMethod.Patch, "/api/user/profile")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { isPublic = false });
        (await _client.SendAsync(patch2)).StatusCode.Should().Be(HttpStatusCode.OK);

        var get2 = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/user/profile").WithUser(TestUsers.UserA));
        var body2 = await get2.Content.ReadFromJsonAsync<JsonElement>();
        body2.GetProperty("isPublic").GetBoolean().Should().BeFalse();
        body2.GetProperty("isMediaListPublic").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task SettingMediaListPublicWhileProfilePrivate_StaysFalse()
    {
        var patch = new HttpRequestMessage(HttpMethod.Patch, "/api/user/profile")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { isMediaListPublic = true });
        (await _client.SendAsync(patch)).StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/user/profile").WithUser(TestUsers.UserA));
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("isMediaListPublic").GetBoolean().Should().BeFalse();
    }
}
