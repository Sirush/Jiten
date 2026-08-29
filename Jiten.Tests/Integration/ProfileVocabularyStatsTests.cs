using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Jiten.Parser.Tests.Integration;

public class ProfileVocabularyStatsTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        // The in-memory Redis outlives ResetDatabaseAsync, so a previous test's entry would answer for this seed.
        var redis = factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        foreach (var userId in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            await redis.KeyDeleteAsync($"jiten:profile-vocab:{userId}");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task ConfigureProfile(string userId, bool isPublic)
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

        await userDb.SaveChangesAsync();
    }

    /// <summary>Seeds one word per tier so word-level counts stay independent of the per-word max-state rule.</summary>
    private async Task SeedCards(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        // ResetDatabaseAsync leaves FSRS tables intact, so the previous test's cards would collide on the unique form key.
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.FsrsCardArchives.ExecuteDeleteAsync();
        await userDb.UserReviewDailies.ExecuteDeleteAsync();

        var now = DateTime.UtcNow;
        userDb.FsrsCards.AddRange(
            new FsrsCard(userId, 100, 0, state: FsrsState.Review, due: now.AddDays(5), lastReview: now.AddDays(-1)),
            new FsrsCard(userId, 200, 0, state: FsrsState.Review, due: now.AddDays(30), lastReview: now.AddDays(-5)),
            new FsrsCard(userId, 300, 0, state: FsrsState.Mastered),
            new FsrsCard(userId, 400, 0, state: FsrsState.Blacklisted));

        await userDb.SaveChangesAsync();
    }

    private async Task SeedMasteredWordSet(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        await userDb.UserWordSetStates.ExecuteDeleteAsync();
        await jitenDb.WordSetMembers.ExecuteDeleteAsync();
        await jitenDb.WordSets.ExecuteDeleteAsync();

        var set = new WordSet { Slug = "test-set", Name = "Test Set", WordCount = 2 };
        jitenDb.WordSets.Add(set);
        await jitenDb.SaveChangesAsync();

        jitenDb.WordSetMembers.AddRange(
            new WordSetMember { SetId = set.SetId, WordId = 900, ReadingIndex = 0, Position = 0 },
            new WordSetMember { SetId = set.SetId, WordId = 901, ReadingIndex = 0, Position = 1 });
        await jitenDb.SaveChangesAsync();

        userDb.UserWordSetStates.Add(new UserWordSetState { UserId = userId, SetId = set.SetId, State = WordSetStateType.Mastered });
        await userDb.SaveChangesAsync();
    }

    [Fact]
    public async Task OwnProfile_ReturnsCounts_EvenWhenPrivate()
    {
        await ConfigureProfile(TestUsers.UserA, isPublic: false);
        await SeedCards(TestUsers.UserA);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{TestUsers.UserA}/vocabulary-stats")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("young").GetInt32().Should().Be(1);
        body.GetProperty("mature").GetInt32().Should().Be(1);
        body.GetProperty("mastered").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Response_OmitsBlacklistedAndFormCounts()
    {
        await ConfigureProfile(TestUsers.UserA, isPublic: true);
        await SeedCards(TestUsers.UserA);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{TestUsers.UserA}/vocabulary-stats");
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.EnumerateObject().Select(p => p.Name).Should()
            .BeEquivalentTo(new[] { "young", "mature", "mastered", "wordSetMastered" },
                            "blacklist state and form-level counts must not leak onto a public profile");
    }

    [Fact]
    public async Task PublicProfile_VisibleToOtherUser()
    {
        await ConfigureProfile(TestUsers.UserA, isPublic: true);
        await SeedCards(TestUsers.UserA);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{TestUsers.UserA}/vocabulary-stats")
            .WithUser(TestUsers.UserB);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("mastered").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task PublicProfile_VisibleAnonymously()
    {
        await ConfigureProfile(TestUsers.UserA, isPublic: true);
        await SeedCards(TestUsers.UserA);

        var response = await _client.GetAsync($"/api/user/profile/{TestUsers.UserA}/vocabulary-stats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("young").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task PrivateProfile_HiddenFromOtherUser()
    {
        await ConfigureProfile(TestUsers.UserA, isPublic: false);
        await SeedCards(TestUsers.UserA);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/profile/{TestUsers.UserA}/vocabulary-stats")
            .WithUser(TestUsers.UserB);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnknownUsername_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/user/profile/nobody-here/vocabulary-stats");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MasteredWordSet_ReportedSeparatelyFromKnownWords()
    {
        await ConfigureProfile(TestUsers.UserA, isPublic: true);
        await SeedCards(TestUsers.UserA);
        await SeedMasteredWordSet(TestUsers.UserA);

        var response = await _client.GetAsync($"/api/user/profile/{TestUsers.UserA}/vocabulary-stats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("wordSetMastered").GetInt32().Should().Be(2);
        body.GetProperty("mastered").GetInt32().Should().Be(1, "word-set words must not inflate the SRS-tracked mastered count");
    }

    [Fact]
    public async Task CountsMatchOwnVocabularyEndpoint()
    {
        await ConfigureProfile(TestUsers.UserA, isPublic: true);
        await SeedCards(TestUsers.UserA);
        await SeedMasteredWordSet(TestUsers.UserA);

        var ownRequest = new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/known-ids/amount")
            .WithUser(TestUsers.UserA);
        var ownResponse = await _client.SendAsync(ownRequest);
        ownResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var own = await ownResponse.Content.ReadFromJsonAsync<JsonElement>();

        var profileResponse = await _client.GetAsync($"/api/user/profile/{TestUsers.UserA}/vocabulary-stats");
        profileResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await profileResponse.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var field in new[] { "young", "mature", "mastered", "wordSetMastered" })
            profile.GetProperty(field).GetInt32().Should().Be(own.GetProperty(field).GetInt32(), $"{field} must match the settings endpoint");
    }

    private async Task AddCards(params FsrsCard[] cards)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.AddRange(cards);
        await userDb.SaveChangesAsync();
    }

    [Fact]
    public async Task WordWithBlacklistedSiblingForm_CountsAsKnown()
    {
        await ConfigureProfile(TestUsers.UserA, isPublic: true);
        await SeedCards(TestUsers.UserA);
        var now = DateTime.UtcNow;
        await AddCards(
            new FsrsCard(TestUsers.UserA, 500, 0, state: FsrsState.Review, due: now.AddDays(30), lastReview: now.AddDays(-5)),
            new FsrsCard(TestUsers.UserA, 500, 1, state: FsrsState.Blacklisted));

        var response = await _client.GetAsync($"/api/user/profile/{TestUsers.UserA}/vocabulary-stats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("mature").GetInt32().Should().Be(2, "a blacklisted sibling form must not disown a word the user knows");
        body.GetProperty("young").GetInt32().Should().Be(1);
        body.GetProperty("mastered").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task WordWithAllFormsBlacklisted_StaysBlacklisted()
    {
        await ConfigureProfile(TestUsers.UserA, isPublic: true);
        await SeedCards(TestUsers.UserA);
        await AddCards(new FsrsCard(TestUsers.UserA, 400, 1, state: FsrsState.Blacklisted));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/known-ids/amount")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("blacklisted").GetInt32().Should().Be(1);
        body.GetProperty("young").GetInt32().Should().Be(1);
        body.GetProperty("mature").GetInt32().Should().Be(1);
        body.GetProperty("mastered").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ResetCard_DoesNotCountAsKnown()
    {
        await ConfigureProfile(TestUsers.UserA, isPublic: true);
        await SeedCards(TestUsers.UserA);
        var now = DateTime.UtcNow;
        // Reviewed but holding no active schedule: back in the new queue, so it counts as neither young nor mature.
        await AddCards(new FsrsCard(TestUsers.UserA, 600, 0, state: FsrsState.New, due: now, lastReview: now.AddDays(-1)));

        var response = await _client.GetAsync($"/api/user/profile/{TestUsers.UserA}/vocabulary-stats");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("young").GetInt32().Should().Be(1);
        body.GetProperty("mature").GetInt32().Should().Be(1);
    }
}
