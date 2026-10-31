using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.CommunityLists;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ProfileTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedDeck(string title, int? parentDeckId = null, bool adult = false, int characters = 1000,
                                     MediaType mediaType = MediaType.Novel)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var deck = new Deck
                   {
                       OriginalTitle = title, MediaType = mediaType, UniqueWordCount = 100, CharacterCount = characters, ParentDeckId = parentDeckId
                   };
        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();
        jitenDb.DeckGenres.Add(new DeckGenre { DeckId = deck.DeckId, Genre = adult ? Genre.AdultOnly : Genre.Mystery });
        await jitenDb.SaveChangesAsync();
        return deck.DeckId;
    }

    private async Task SetStatus(string userId, int deckId, DeckStatus status, bool isPrivate = false, short? score = null)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserDeckPreferences.Add(new UserDeckPreference
                                       {
                                           UserId = userId, DeckId = deckId, Status = status, IsPrivate = isPrivate, Score = score,
                                           UpdatedAt = DateTime.UtcNow
                                       });
        await userDb.SaveChangesAsync();
    }

    private async Task SetPrivacy(string userId, string json)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var profile = await userDb.UserProfiles.FirstAsync(p => p.UserId == userId);
        profile.PrivacyJson = json;
        await userDb.SaveChangesAsync();
    }

    /// <summary>The database reset keeps user rows, so a display name set by an earlier test survives into the next.</summary>
    private async Task ClearDisplayName(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var user = await userDb.Users.FirstAsync(u => u.Id == userId);
        user.DisplayName = null;
        user.NormalizedDisplayName = null;
        await userDb.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> Get(string url, string? asUser)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (asUser != null) request.WithUser(asUser);
        return _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string asUser, object? body = null)
    {
        var request = new HttpRequestMessage(method, url).WithUser(asUser);
        if (body != null) request.WithJsonContent(body);
        return _client.SendAsync(request);
    }

    private static List<string> MediaListTitles(JsonElement list) =>
        list.EnumerateArray().Select(d => d.GetProperty("originalTitle").GetString()!).ToList();

    private static List<JsonValueKind> ViewerStatusKinds(JsonElement list) =>
        list.EnumerateArray().Select(d => d.TryGetProperty("viewerStatus", out var status) ? status.ValueKind : JsonValueKind.Null).ToList();

    [Fact]
    public async Task PrivateProfileIsNotFoundForOthersButOpensForItsOwner()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"private"}""");

        (await Get($"/api/user/profile/{TestUsers.UserA}", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get($"/api/user/profile/{TestUsers.UserA}", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get($"/api/user/profile/{TestUsers.UserA}", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HiddenProfileByIdAnswersLikeAMissingOne()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"private"}""");

        foreach (var url in new[] { "accomplishments", "accomplishments/global", "accomplishments/vocabulary" })
        {
            (await Get($"/api/user/user/{TestUsers.UserA}/{url}", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await Get($"/api/user/user/{Guid.NewGuid()}/{url}", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task HiddenKnownCountsAreNotFoundOnTheVocabularyTab()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"public","hiddenFields":["wordsKnown","kanjiKnown"]}""");

        foreach (var endpoint in new[] { "vocabulary-stats", "kanji-grid" })
        {
            (await Get($"/api/user/profile/{TestUsers.UserA}/{endpoint}", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await Get($"/api/user/profile/{TestUsers.UserA}/{endpoint}", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task PublicProfileWithoutDisplayNameIsHiddenFromOthers()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await ClearDisplayName(TestUsers.UserA);

        (await Get($"/api/user/profile/{TestUsers.UserA}", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task HeaderResolvesByDisplayNameAndNeverReturnsTheLoginName()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var user = await userDb.Users.FirstAsync(u => u.Id == TestUsers.UserA);
            user.DisplayName = "Hoshikawa";
            user.NormalizedDisplayName = "HOSHIKAWA";
            await userDb.SaveChangesAsync();
        }

        var byName = await Get("/api/user/profile/hoshikawa", TestUsers.UserB);
        byName.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await byName.Content.ReadAsStringAsync();
        body.Should().NotContain(TestUsers.UserA);
        JsonDocument.Parse(body).RootElement.GetProperty("displayName").GetString().Should().Be("Hoshikawa");
        JsonDocument.Parse(body).RootElement.GetProperty("privacy").ValueKind.Should().Be(JsonValueKind.Null);

        var legacy = await Get($"/api/user/profile/{TestUsers.UserA}", TestUsers.UserB);
        legacy.StatusCode.Should().Be(HttpStatusCode.OK);
        (await legacy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("displayName").GetString().Should().Be("Hoshikawa");
    }

    [Fact]
    public async Task MemberSinceGivesOnlyTheJoinMonth()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var user = await userDb.Users.FirstAsync(u => u.Id == TestUsers.UserA);
            user.CreatedAt = new DateTime(2024, 3, 17, 14, 52, 9, DateTimeKind.Utc);
            await userDb.SaveChangesAsync();
        }

        var header = await (await Get($"/api/user/profile/{TestUsers.UserA}", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();

        header.GetProperty("memberSince").GetDateTime().ToUniversalTime().Should().Be(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task MakingProfileVisibleRequiresADisplayName()
    {
        await ClearDisplayName(TestUsers.UserA);
        var response = await Send(HttpMethod.Patch, "/api/user/profile", TestUsers.UserA,
                                  new { privacy = new { profile = "public", sections = new Dictionary<string, string>(), hiddenFields = Array.Empty<string>(), hideAdultTitles = true } });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PrivacyRoundTripsAndMirrorsTheLegacyFlags()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);

        var response = await Send(HttpMethod.Patch, "/api/user/profile", TestUsers.UserA,
                                  new
                                  {
                                      privacy = new
                                                {
                                                    profile = "public", sections = new Dictionary<string, string> { ["library"] = "everyone", ["stats"] = "onlyMe" },
                                                    hiddenFields = new[] { "links" }, hideAdultTitles = false
                                                }
                                  });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var privacy = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("privacy");
        privacy.GetProperty("sections").GetProperty("stats").GetString().Should().Be("onlyMe");
        privacy.GetProperty("hiddenFields").EnumerateArray().Select(f => f.GetString()).Should().Equal("links");
        privacy.GetProperty("hideAdultTitles").GetBoolean().Should().BeFalse();

        using var scope = factory.Services.CreateScope();
        var profile = await scope.ServiceProvider.GetRequiredService<UserDbContext>().UserProfiles.AsNoTracking().FirstAsync(p => p.UserId == TestUsers.UserA);
        profile.IsPublic.Should().BeTrue();
        profile.IsMediaListPublic.Should().BeTrue();

        (await Get($"/api/user/profile/{TestUsers.UserA}/stats", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get($"/api/user/profile/{TestUsers.UserA}/stats", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PrivateAndAdultTitlesAreLeftOutForOthers()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: true);
        var open = await SeedDeck("Open Title");
        var hidden = await SeedDeck("Private Title");
        var adult = await SeedDeck("Adult Title", adult: true);
        await SetStatus(TestUsers.UserA, open, DeckStatus.Completed);
        await SetStatus(TestUsers.UserA, hidden, DeckStatus.Completed, isPrivate: true);
        await SetStatus(TestUsers.UserA, adult, DeckStatus.Completed);

        var others = await (await Get($"/api/user/profile/{TestUsers.UserA}/media-list", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        MediaListTitles(others).Should().Equal("Open Title");

        var own = await (await Get($"/api/user/profile/{TestUsers.UserA}/media-list", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        MediaListTitles(own).Should().BeEquivalentTo("Open Title", "Private Title", "Adult Title");

        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"library":"everyone"},"hideAdultTitles":false}""");
        var withAdult = await (await Get($"/api/user/profile/{TestUsers.UserA}/media-list", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        MediaListTitles(withAdult).Should().BeEquivalentTo("Open Title", "Adult Title");
    }

    [Fact]
    public async Task MediaListCarriesTheViewersOwnStatusForOthersOnly()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: true);
        var shared = await SeedDeck("Shared Title");
        var theirsOnly = await SeedDeck("Their Title");
        await SetStatus(TestUsers.UserA, shared, DeckStatus.Ongoing);
        await SetStatus(TestUsers.UserA, theirsOnly, DeckStatus.Ongoing);
        await SetStatus(TestUsers.UserB, shared, DeckStatus.Planning);

        var others = await (await Get($"/api/user/profile/{TestUsers.UserA}/media-list", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        var viewerStatuses = others.EnumerateArray().ToDictionary(d => d.GetProperty("originalTitle").GetString()!, d => d.GetProperty("viewerStatus").GetInt32());
        viewerStatuses.Should().Equal(new Dictionary<string, int> { ["Shared Title"] = (int)DeckStatus.Planning, ["Their Title"] = (int)DeckStatus.None });

        var own = await (await Get($"/api/user/profile/{TestUsers.UserA}/media-list", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        ViewerStatusKinds(own).Should().OnlyContain(k => k == JsonValueKind.Null);

        var anonymous = await (await Get($"/api/user/profile/{TestUsers.UserA}/media-list", null)).Content.ReadFromJsonAsync<JsonElement>();
        ViewerStatusKinds(anonymous).Should().OnlyContain(k => k == JsonValueKind.Null);
    }

    [Fact]
    public async Task LibrarySectionHidesCurrentlyReadingAndGenres()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: false);
        var reading = await SeedDeck("Reading Now");
        await SetStatus(TestUsers.UserA, reading, DeckStatus.Ongoing);

        var others = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        others.GetProperty("currentlyReading").ValueKind.Should().Be(JsonValueKind.Null);
        others.GetProperty("genres").ValueKind.Should().Be(JsonValueKind.Null);

        var own = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        own.GetProperty("currentlyReading").EnumerateArray().Select(d => d.GetProperty("deck").GetProperty("originalTitle").GetString())
           .Should().Equal("Reading Now");
    }

    [Fact]
    public async Task HiddenFieldIsNullInTheSnapshot()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"public","hiddenFields":["wordsKnown"]}""");

        var others = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        others.GetProperty("snapshot").GetProperty("wordsKnown").ValueKind.Should().Be(JsonValueKind.Null);
        others.GetProperty("snapshot").GetProperty("kanjiKnown").ValueKind.Should().Be(JsonValueKind.Number);

        var own = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        own.GetProperty("snapshot").GetProperty("wordsKnown").ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task FinishedTotalsShowWithoutLibraryOrStatsAndHideByField()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"library":"onlyMe","stats":"onlyMe"}}""");

        var shown = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        shown.GetProperty("snapshot").GetProperty("characters").ValueKind.Should().Be(JsonValueKind.Number);
        shown.GetProperty("snapshot").GetProperty("titlesFinished").ValueKind.Should().Be(JsonValueKind.Number);

        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"library":"onlyMe","stats":"everyone"},"hiddenFields":["titlesFinished"]}""");

        var hidden = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        hidden.GetProperty("snapshot").GetProperty("titlesFinished").ValueKind.Should().Be(JsonValueKind.Null);
        var stats = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        stats.GetProperty("titlesFinished").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task StatsLeaveOutGenresAndTitleNamesWhileTheLibraryIsHidden()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"library":"onlyMe","stats":"everyone"}}""");
        await SetStatus(TestUsers.UserA, await SeedDeck("Finished"), DeckStatus.Completed);

        var others = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        others.GetProperty("genres").ValueKind.Should().Be(JsonValueKind.Null);
        others.GetProperty("mediaTypes").GetArrayLength().Should().Be(1);

        var own = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        own.GetProperty("genres").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task PrivacyUpdateLeavesOmittedSettingsAlone()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"public","hiddenFields":["links"],"hideAdultTitles":false}""");

        var response = await Send(HttpMethod.Patch, "/api/user/profile", TestUsers.UserA,
                                  new { privacy = new { sections = new Dictionary<string, string> { ["stats"] = "onlyMe" } } });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var privacy = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("privacy");
        privacy.GetProperty("profile").GetString().Should().Be("public");
        privacy.GetProperty("hiddenFields").EnumerateArray().Select(f => f.GetString()).Should().Equal("links");
        privacy.GetProperty("hideAdultTitles").GetBoolean().Should().BeFalse();
        privacy.GetProperty("sections").GetProperty("stats").GetString().Should().Be("onlyMe");
    }

    [Fact]
    public async Task DeletingAPreferenceUnpinsTheTitle()
    {
        var deck = await SeedDeck("Pinned then deleted");
        await SetStatus(TestUsers.UserA, deck, DeckStatus.Completed);
        await Send(HttpMethod.Post, $"/api/user/profile/pinned/{deck}", TestUsers.UserA);

        (await Send(HttpMethod.Delete, $"/api/user/deck-preferences/{deck}", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await (await Get("/api/user/profile/pinned", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task PinsAreCappedAndRefusePrivateTitles()
    {
        var ids = new List<int>();
        for (var i = 0; i < 7; i++)
            ids.Add(await SeedDeck($"Title {i}"));

        (await Send(HttpMethod.Put, "/api/user/profile/pinned", TestUsers.UserA, new { deckIds = ids })).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Put, "/api/user/profile/pinned", TestUsers.UserA, new { deckIds = ids.Take(6) })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Post, $"/api/user/profile/pinned/{ids[6]}", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        await SetStatus(TestUsers.UserA, ids[6], DeckStatus.Completed, isPrivate: true);
        await Send(HttpMethod.Delete, $"/api/user/profile/pinned/{ids[0]}", TestUsers.UserA);
        (await Send(HttpMethod.Post, $"/api/user/profile/pinned/{ids[6]}", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PinnedListReturnsOwnPinsInOrderByMainEntry()
    {
        var first = await SeedDeck("First");
        var series = await SeedDeck("Series");
        var volume = await SeedDeck("Volume 1", parentDeckId: series);
        await Send(HttpMethod.Post, $"/api/user/profile/pinned/{volume}", TestUsers.UserA);
        await Send(HttpMethod.Post, $"/api/user/profile/pinned/{first}", TestUsers.UserA);

        var pins = await (await Get("/api/user/profile/pinned", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();

        pins.EnumerateArray().Select(p => p.GetProperty("deckId").GetInt32()).Should().Equal(series, first);
        pins[0].GetProperty("originalTitle").GetString().Should().Be("Series");
        (await Get("/api/user/profile/pinned", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>().Result.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task APinnedTitleMadePrivateStaysPinnedButOnlyItsOwnerSeesIt()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: true);
        var deck = await SeedDeck("Pinned");
        var other = await SeedDeck("Other");
        await SetStatus(TestUsers.UserA, deck, DeckStatus.Completed);
        await Send(HttpMethod.Post, $"/api/user/profile/pinned/{deck}", TestUsers.UserA);

        var response = await Send(HttpMethod.Patch, $"/api/user/deck-preferences/{deck}/details", TestUsers.UserA, new { isPrivate = true });

        var details = await response.Content.ReadFromJsonAsync<JsonElement>();
        details.GetProperty("isPrivate").GetBoolean().Should().BeTrue();
        details.GetProperty("isPinned").GetBoolean().Should().BeTrue();

        async Task<List<int>> OverviewPins(string viewer) =>
            (await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", viewer)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("pinned").EnumerateArray().Select(p => p.GetProperty("deck").GetProperty("deckId").GetInt32()).ToList();

        (await OverviewPins(TestUsers.UserA)).Should().Equal(deck);
        (await OverviewPins(TestUsers.UserB)).Should().BeEmpty();
        (await (await Get("/api/user/profile/pinned", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength().Should().Be(1);

        (await Send(HttpMethod.Put, "/api/user/profile/pinned", TestUsers.UserA, new { deckIds = new[] { other, deck } })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Post, $"/api/user/profile/pinned/{deck}", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.OK);

        await Send(HttpMethod.Patch, $"/api/user/deck-preferences/{deck}/details", TestUsers.UserA, new { isPrivate = false });
        (await OverviewPins(TestUsers.UserB)).Should().Equal(other, deck);
    }

    [Fact]
    public async Task DetailsAreStoredOnTheMainEntryAndValidated()
    {
        var series = await SeedDeck("Series");
        var volume = await SeedDeck("Volume 1", parentDeckId: series);

        (await Send(HttpMethod.Patch, $"/api/user/deck-preferences/{volume}/details", TestUsers.UserA, new { setScore = true, score = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Send(HttpMethod.Patch, $"/api/user/deck-preferences/{volume}/details", TestUsers.UserA, new { note = new string('a', 501) }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var response = await Send(HttpMethod.Patch, $"/api/user/deck-preferences/{volume}/details", TestUsers.UserA,
                                  new { setScore = true, score = 85, note = "  slow start  " });
        var details = await response.Content.ReadFromJsonAsync<JsonElement>();
        details.GetProperty("deckId").GetInt32().Should().Be(series);
        details.GetProperty("score").GetDecimal().Should().Be(8.5m);
        details.GetProperty("note").GetString().Should().Be("slow start");

        var cleared = await (await Send(HttpMethod.Patch, $"/api/user/deck-preferences/{series}/details", TestUsers.UserA,
                                        new { setScore = true, score = (short?)null })).Content.ReadFromJsonAsync<JsonElement>();
        cleared.GetProperty("score").ValueKind.Should().Be(JsonValueKind.Null);
        cleared.GetProperty("note").GetString().Should().Be("slow start");
    }

    [Fact]
    public async Task NotesNeverReachAnotherUser()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: true);
        var deck = await SeedDeck("Noted");
        await SetStatus(TestUsers.UserA, deck, DeckStatus.Completed, score: 90);
        await Send(HttpMethod.Patch, $"/api/user/deck-preferences/{deck}/details", TestUsers.UserA, new { note = "secret thought" });
        await Send(HttpMethod.Post, $"/api/user/profile/pinned/{deck}", TestUsers.UserA);

        foreach (var url in new[] { "", "/overview", "/media-list", "/stats" })
        {
            var body = await (await Get($"/api/user/profile/{TestUsers.UserA}{url}", TestUsers.UserB)).Content.ReadAsStringAsync();
            body.Should().NotContain("secret thought");
        }

        var overview = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        overview.GetProperty("pinned")[0].GetProperty("score").GetDecimal().Should().Be(9.0m);
    }

    [Fact]
    public async Task LeavingTheListKeepsScoreAndNote()
    {
        var deck = await SeedDeck("Kept");
        await SetStatus(TestUsers.UserA, deck, DeckStatus.Completed, score: 70);

        await Send(HttpMethod.Post, $"/api/user/deck-preferences/{deck}/status", TestUsers.UserA, new { status = DeckStatus.None });

        using var scope = factory.Services.CreateScope();
        var pref = await scope.ServiceProvider.GetRequiredService<UserDbContext>().UserDeckPreferences.AsNoTracking()
                              .FirstOrDefaultAsync(p => p.UserId == TestUsers.UserA && p.DeckId == deck);
        pref.Should().NotBeNull();
        pref!.Status.Should().Be(DeckStatus.None);
        pref.Score.Should().Be(70);
    }

    [Fact]
    public async Task MilestonesReportTierAndNextStep()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserAccomplishments.Add(new UserAccomplishment
                                           {
                                               UserId = TestUsers.UserA, MediaType = null, CompletedDeckCount = 12, CompletedUnitCount = 12,
                                               TotalCharacterCount = 50_000
                                           });
            await userDb.SaveChangesAsync();
        }

        var overview = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        var milestones = overview.GetProperty("milestones").EnumerateArray().ToDictionary(m => m.GetProperty("kind").GetString()!);

        milestones["titles"].GetProperty("tier").GetString().Should().Be("ichibu");
        milestones["titles"].GetProperty("nextTier").GetString().Should().Be("koban");
        milestones["titles"].GetProperty("nextThreshold").GetInt64().Should().Be(25);
        milestones["characters"].GetProperty("tier").GetString().Should().Be("zeni");
        milestones["characters"].GetProperty("nextThreshold").GetInt64().Should().Be(250_000);
    }

    [Fact]
    public async Task VoteMilestoneCountsHandMadeVotesAndRatingsOnly()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"stats":"everyone"}}""");
        var decks = new List<int>();
        for (var i = 0; i < 6; i++)
            decks.Add(await SeedDeck($"Deck {i}"));
        var pairs = (from a in decks from b in decks where a < b select (Low: a, High: b)).ToList();
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            DifficultyVote Vote(int index, DifficultyVoteSource source, bool valid = true) => new()
            {
                UserId = TestUsers.UserA, DeckLowId = pairs[index].Low, DeckHighId = pairs[index].High, Outcome = ComparisonOutcome.Harder,
                Source = source, IsValid = valid, CreatedAt = DateTimeOffset.UtcNow
            };
            jitenDb.DifficultyVotes.AddRange(Enumerable.Range(0, 8).Select(i => Vote(i, DifficultyVoteSource.Manual)));
            jitenDb.DifficultyVotes.Add(Vote(8, DifficultyVoteSource.Manual, valid: false));
            jitenDb.DifficultyVotes.AddRange(Enumerable.Range(9, 5).Select(i => Vote(i, DifficultyVoteSource.WeakOrder)));
            jitenDb.DifficultyRatings.AddRange(
                new DifficultyRating { UserId = TestUsers.UserA, DeckId = decks[0], Rating = 2, CreatedAt = DateTimeOffset.UtcNow },
                new DifficultyRating { UserId = TestUsers.UserA, DeckId = decks[1], Rating = 3, CreatedAt = DateTimeOffset.UtcNow });
            await jitenDb.SaveChangesAsync();
        }

        var overview = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        var votes = overview.GetProperty("milestones").EnumerateArray().Single(m => m.GetProperty("kind").GetString() == "votes");

        votes.GetProperty("value").GetInt64().Should().Be(10);
        votes.GetProperty("tier").GetString().Should().Be("zeni");
    }

    [Fact]
    public async Task HiddenMilestonesLeaveTheSnapshotVisible()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"milestones":"onlyMe"}}""");
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserAccomplishments.Add(new UserAccomplishment
                                           {
                                               UserId = TestUsers.UserA, MediaType = null, CompletedDeckCount = 12, CompletedUnitCount = 12,
                                               TotalCharacterCount = 50_000
                                           });
            await userDb.SaveChangesAsync();
        }

        var others = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        others.GetProperty("milestones").ValueKind.Should().Be(JsonValueKind.Null);
        others.GetProperty("snapshot").GetProperty("titlesFinished").GetInt32().Should().Be(12);

        var preview = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview?viewAs=everyone", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        preview.GetProperty("milestones").ValueKind.Should().Be(JsonValueKind.Null);

        var own = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        own.GetProperty("milestones").EnumerateArray().Select(m => m.GetProperty("kind").GetString()).Should().Contain("titles");
    }

    private async Task<long> AddEntry(int deckId, MediaListEntryState state, DateOnly? finishedOn, int? charactersRead = null, long? seriesEntryId = null)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var entry = new UserMediaListEntry
                    {
                        UserId = TestUsers.UserA, DeckId = deckId, State = state, FinishedOn = finishedOn, CharactersRead = charactersRead,
                        SeriesEntryId = seriesEntryId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                    };
        userDb.UserMediaListEntries.Add(entry);
        await userDb.SaveChangesAsync();
        return entry.Id;
    }

    [Fact]
    public async Task StatsCountFinishedTitlesOnce_AndReadingByPass()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"stats":"everyone"}}""");

        var series = await SeedDeck("Series", characters: 3000);
        var volume1 = await SeedDeck("Volume 1", parentDeckId: series);
        var volume2 = await SeedDeck("Volume 2", parentDeckId: series);
        await SetStatus(TestUsers.UserA, series, DeckStatus.Completed);
        var pass = await AddEntry(series, MediaListEntryState.Completed, new DateOnly(2026, 3, 20));
        await AddEntry(volume1, MediaListEntryState.Completed, new DateOnly(2026, 1, 10), seriesEntryId: pass);
        await AddEntry(volume2, MediaListEntryState.Completed, new DateOnly(2026, 2, 10), seriesEntryId: pass);

        var reread = await SeedDeck("Read twice");
        await SetStatus(TestUsers.UserA, reread, DeckStatus.Completed);
        await AddEntry(reread, MediaListEntryState.Completed, new DateOnly(2026, 1, 5), charactersRead: 600);
        await AddEntry(reread, MediaListEntryState.Completed, new DateOnly(2026, 4, 1));

        var partSeries = await SeedDeck("Part-read series", characters: 2000);
        var partVolume = await SeedDeck("Part-read volume", parentDeckId: partSeries);
        await SetStatus(TestUsers.UserA, partVolume, DeckStatus.Completed);
        await AddEntry(partVolume, MediaListEntryState.Completed, new DateOnly(2026, 4, 2));

        var reading = await SeedDeck("Reading");
        await SetStatus(TestUsers.UserA, reading, DeckStatus.Ongoing);
        await AddEntry(reading, MediaListEntryState.InProgress, null, charactersRead: 250);

        var own = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        own.GetProperty("difficulty").EnumerateArray().Sum(d => d.GetProperty("count").GetInt32()).Should().Be(2);
        own.GetProperty("lengths").EnumerateArray().Sum(d => d.GetProperty("count").GetInt32()).Should().Be(2);
        own.GetProperty("charactersByMonth").EnumerateArray()
           .ToDictionary(m => m.GetProperty("month").GetString()!, m => m.GetProperty("characters").GetInt64())
           .Should().Equal(new Dictionary<string, long> { ["2026-01"] = 1600, ["2026-02"] = 1000, ["2026-03"] = 1000, ["2026-04"] = 2000 });
        var ownTypes = own.GetProperty("mediaTypes")[0];
        ownTypes.GetProperty("count").GetInt32().Should().Be(2);
        ownTypes.GetProperty("characters").GetInt64().Should().Be(5850);

        var others = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        others.GetProperty("mediaTypes")[0].GetProperty("characters").GetInt64().Should().Be(5600);
    }

    [Fact]
    public async Task LadderShowsSplitTypesAsTheirDatedVolumes()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"stats":"everyone"}}""");

        var series = await SeedDeck("Series", characters: 3000);
        var volumes = new[] { await SeedDeck("Volume 1", series), await SeedDeck("Volume 2", series), await SeedDeck("Volume 3", series) };
        await SetStatus(TestUsers.UserA, series, DeckStatus.Completed);
        await AddEntry(series, MediaListEntryState.Completed, new DateOnly(2026, 3, 1));
        await AddEntry(volumes[0], MediaListEntryState.Completed, new DateOnly(2026, 1, 1));
        await AddEntry(volumes[1], MediaListEntryState.Completed, new DateOnly(2026, 2, 1));
        await AddEntry(volumes[2], MediaListEntryState.Completed, null);

        var partSeries = await SeedDeck("Part-read series");
        await AddEntry(await SeedDeck("Part-read volume", partSeries), MediaListEntryState.Completed, new DateOnly(2026, 4, 1));

        var manga = await SeedDeck("Manga", mediaType: MediaType.Manga);
        await SetStatus(TestUsers.UserA, manga, DeckStatus.Completed);
        await AddEntry(await SeedDeck("Manga volume", manga, mediaType: MediaType.Manga), MediaListEntryState.Completed, new DateOnly(2026, 5, 1));

        var whole = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats/ladder", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        whole.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("title").GetString()).Should().Equal("Series", "Manga");
        whole.GetProperty("points")[0].GetProperty("finishedOn").GetString().Should().Be("2026-03-01");
        whole.GetProperty("volumeTypes").EnumerateArray().Select(t => t.GetInt32()).Should().Equal((int)MediaType.Novel, (int)MediaType.Manga);

        var split = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats/ladder?split={(int)MediaType.Novel}", TestUsers.UserA))
                          .Content.ReadFromJsonAsync<JsonElement>();
        split.GetProperty("points").EnumerateArray().Select(p => p.GetProperty("volumeTitle").GetString() ?? p.GetProperty("title").GetString())
             .Should().Equal("Volume 1", "Volume 2", "Part-read volume", "Manga");
        split.GetProperty("undatedVolumes").GetInt32().Should().Be(1);
        split.GetProperty("undatedTitles").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task CharactersMatchAcrossSectionsAndKeepProgressPrivate()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"stats":"everyone"}}""");
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserAccomplishments.Add(new UserAccomplishment
                                           {
                                               UserId = TestUsers.UserA, MediaType = null, CompletedDeckCount = 3, CompletedUnitCount = 3,
                                               TotalCharacterCount = 120_000, UnfinishedCharacterCount = 5_000
                                           });
            await userDb.SaveChangesAsync();
        }

        foreach (var (viewer, characters) in new[] { (TestUsers.UserA, 125_000L), (TestUsers.UserB, 120_000L) })
        {
            var overview = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", viewer)).Content.ReadFromJsonAsync<JsonElement>();
            overview.GetProperty("snapshot").GetProperty("characters").GetInt64().Should().Be(characters);
            var stats = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats", viewer)).Content.ReadFromJsonAsync<JsonElement>();
            stats.GetProperty("characters").GetInt64().Should().Be(characters);
        }

        var othersTotals = await (await Get($"/api/user/profile/{TestUsers.UserA}/accomplishments", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        othersTotals[0].GetProperty("unfinishedCharacterCount").GetInt64().Should().Be(0);

        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"stats":"everyone"},"hiddenFields":["characters"]}""");
        var hiddenStats = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        hiddenStats.GetProperty("characters").ValueKind.Should().Be(JsonValueKind.Null);
        var hiddenTotals = await (await Get($"/api/user/profile/{TestUsers.UserA}/accomplishments", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        hiddenTotals[0].GetProperty("totalCharacterCount").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task FinishedVocabularyLeavesOutPrivateAndAdultTitles()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: true);
        var decks = new[] { await SeedDeck("Open Title"), await SeedDeck("Private Title"), await SeedDeck("Adult Title", adult: true) };
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            for (var i = 0; i < decks.Length; i++)
                jitenDb.DeckWords.Add(new DeckWord { DeckId = decks[i], WordId = 1_000 + i, ReadingIndex = 0, Occurrences = 5 });
            await jitenDb.SaveChangesAsync();
        }
        await SetStatus(TestUsers.UserA, decks[0], DeckStatus.Completed);
        await SetStatus(TestUsers.UserA, decks[1], DeckStatus.Completed, isPrivate: true);
        await SetStatus(TestUsers.UserA, decks[2], DeckStatus.Completed);

        async Task<int> WordCount(string url, string viewer) =>
            (await (await Get(url, viewer)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalItems").GetInt32();

        foreach (var url in new[] { $"/api/user/profile/{TestUsers.UserA}/accomplishments/vocabulary", $"/api/user/user/{TestUsers.UserA}/accomplishments/vocabulary" })
        {
            (await WordCount(url, TestUsers.UserB)).Should().Be(1);
            (await WordCount(url, TestUsers.UserA)).Should().Be(3);
        }
    }

    [Fact]
    public async Task HiddenLibraryKeepsTitlesOffTheStatsLadderAndPinnedStatus()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true, isMediaListPublic: false);
        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"stats":"everyone"}}""");
        var decks = new[] { await SeedDeck("First Finish"), await SeedDeck("Second Finish") };
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            for (var i = 0; i < decks.Length; i++)
                userDb.UserMediaListEntries.Add(new UserMediaListEntry
                                                {
                                                    UserId = TestUsers.UserA, DeckId = decks[i], State = MediaListEntryState.Completed,
                                                    FinishedOn = new DateOnly(2026, 1 + i, 1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                                                });
            await userDb.SaveChangesAsync();
        }
        foreach (var deck in decks)
            await SetStatus(TestUsers.UserA, deck, DeckStatus.Completed);
        await Send(HttpMethod.Post, $"/api/user/profile/pinned/{decks[0]}", TestUsers.UserA);

        var others = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats/ladder", TestUsers.UserB)).Content.ReadAsStringAsync();
        others.Should().NotContain("First Finish").And.NotContain("Second Finish");
        var ladder = JsonDocument.Parse(others).RootElement.GetProperty("points");
        ladder.GetArrayLength().Should().Be(2);
        ladder[0].GetProperty("deckId").ValueKind.Should().Be(JsonValueKind.Null);

        var own = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats/ladder", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        own.GetProperty("points")[0].GetProperty("title").GetString().Should().Be("First Finish");

        var overview = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        overview.GetProperty("pinned")[0].GetProperty("status").GetInt32().Should().Be((int)DeckStatus.None);
    }

    [Fact]
    public async Task ViewAsEveryoneShowsTheOwnerWhatVisitorsSee()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"library":"onlyMe"}}""");

        var own = await (await Get($"/api/user/profile/{TestUsers.UserA}", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        own.GetProperty("isOwner").GetBoolean().Should().BeTrue();
        own.GetProperty("previewAs").ValueKind.Should().Be(JsonValueKind.Null);

        var preview = await (await Get($"/api/user/profile/{TestUsers.UserA}?viewAs=everyone", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        preview.GetProperty("isOwner").GetBoolean().Should().BeFalse();
        preview.GetProperty("previewAs").GetString().Should().Be("everyone");
        preview.GetProperty("privacy").ValueKind.Should().Be(JsonValueKind.Null);
        preview.GetProperty("sections").EnumerateArray().Select(e => e.GetString()).Should().NotContain("library");

        (await Get($"/api/user/profile/{TestUsers.UserA}/media-list?viewAs=everyone", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get($"/api/user/profile/{TestUsers.UserA}/media-list", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ViewAsNeverWidensWhatAVisitorSees()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"private"}""");

        (await Get($"/api/user/profile/{TestUsers.UserA}?viewAs=everyone", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Get($"/api/user/profile/{TestUsers.UserA}?viewAs=owner", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BulkPrivacyHidesTitlesByMainEntryAndKeepsTheirPins()
    {
        var series = await SeedDeck("Series");
        var volume = await SeedDeck("Volume", parentDeckId: series);
        var other = await SeedDeck("Other");
        await SetStatus(TestUsers.UserA, series, DeckStatus.Completed);
        await Send(HttpMethod.Post, $"/api/user/profile/pinned/{series}", TestUsers.UserA);

        var hidden = await Send(HttpMethod.Post, "/api/user/deck-preferences/bulk", TestUsers.UserA, new { deckIds = new[] { volume, other }, isPrivate = true });
        hidden.StatusCode.Should().Be(HttpStatusCode.OK);
        (await hidden.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("affected").GetInt32().Should().Be(2);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            (await userDb.UserDeckPreferences.Where(p => p.UserId == TestUsers.UserA && p.IsPrivate).Select(p => p.DeckId).ToListAsync())
                .Should().BeEquivalentTo([series, other]);
            (await userDb.UserPinnedDecks.AnyAsync(p => p.UserId == TestUsers.UserA && p.DeckId == series)).Should().BeTrue();
        }

        var shown = await Send(HttpMethod.Post, "/api/user/deck-preferences/bulk", TestUsers.UserA, new { deckIds = new[] { series }, isPrivate = false });
        (await shown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("affected").GetInt32().Should().Be(1);

        (await Send(HttpMethod.Post, "/api/user/deck-preferences/bulk", TestUsers.UserA, new { deckIds = new[] { series }, isPrivate = true, isFavourite = true }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AvatarStyleRoundTripsAndRejectsUnknownValues()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);

        var saved = await Send(HttpMethod.Patch, "/api/user/profile", TestUsers.UserA, new { avatarStyle = "seal" });
        (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("avatarStyle").GetString().Should().Be("seal");

        var header = await (await Get($"/api/user/profile/{TestUsers.UserA}", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        header.GetProperty("avatarStyle").GetString().Should().Be("seal");
        header.GetProperty("avatarStrokes").ValueKind.Should().Be(JsonValueKind.Null);

        (await Send(HttpMethod.Patch, "/api/user/profile", TestUsers.UserA, new { avatarStyle = "outline" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var reset = await Send(HttpMethod.Patch, "/api/user/profile", TestUsers.UserA, new { avatarStyle = "filled" });
        (await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("avatarStyle").GetString().Should().Be("filled");
    }

    [Fact]
    public async Task CurrentUserCarriesTheProfileAvatar()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await Send(HttpMethod.Patch, "/api/user/profile", TestUsers.UserA, new { avatarKanji = "夢", avatarColour = "teal", avatarStyle = "seal" });

        var me = await (await Get("/api/auth/me", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        me.GetProperty("avatarKanji").GetString().Should().Be("夢");
        me.GetProperty("avatarColour").GetString().Should().Be("teal");
        me.GetProperty("avatarStyle").GetString().Should().Be("seal");
    }

    [Fact]
    public async Task StrokeAvatarCarriesTheCharactersStrokes()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            if (!await jitenDb.KanjiStrokes.AnyAsync(k => k.Character == "夢"))
                jitenDb.KanjiStrokes.Add(new Jiten.Core.Data.JMDict.KanjiStrokes { Character = "夢", Paths = ["M1,1L2,2", "M3,3L4,4"] });
            await jitenDb.SaveChangesAsync();
        }

        await Send(HttpMethod.Patch, "/api/user/profile", TestUsers.UserA, new { avatarStyle = "stroke", avatarKanji = "夢" });
        var header = await (await Get($"/api/user/profile/{TestUsers.UserA}", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        header.GetProperty("avatarStrokes").GetArrayLength().Should().Be(2);

        await Send(HttpMethod.Patch, "/api/user/profile", TestUsers.UserA, new { avatarKanji = "あ" });
        header = await (await Get($"/api/user/profile/{TestUsers.UserA}", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        header.GetProperty("avatarStyle").GetString().Should().Be("stroke");
        header.GetProperty("avatarStrokes").ValueKind.Should().Be(JsonValueKind.Null);

        var batch = await (await Get("/api/kanji/strokes?characters=夢あ", null)).Content.ReadFromJsonAsync<Dictionary<string, List<string>>>();
        batch.Should().ContainKey("夢").And.HaveCount(1);
    }

    private async Task<int> SeedList(string ownerId, string name, CommunityListStatus status, string? favouritedBy = null)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var list = new CommunityList
                   {
                       OwnerId = ownerId, Name = name, Slug = name.ToLowerInvariant(), Status = status, CreatedAt = DateTime.UtcNow,
                       UpdatedAt = DateTime.UtcNow, PublishedAt = status == CommunityListStatus.Public ? DateTime.UtcNow : null
                   };
        jitenDb.CommunityLists.Add(list);
        await jitenDb.SaveChangesAsync();
        if (favouritedBy != null)
        {
            jitenDb.CommunityListFavourites.Add(new CommunityListFavourite { ListId = list.ListId, UserId = favouritedBy, CreatedAt = DateTime.UtcNow });
            await jitenDb.SaveChangesAsync();
        }

        return list.ListId;
    }

    [Fact]
    public async Task ListsTabShowsPublicListsAndSharesFavouritesOnlyWhenAllowed()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await factory.ConfigureProfileAsync(TestUsers.UserB, isPublic: true);

        async Task<bool> HasLists(string viewer) =>
            (await (await Get($"/api/user/profile/{TestUsers.UserA}", viewer)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasLists").GetBoolean();

        (await HasLists(TestUsers.UserB)).Should().BeFalse();

        await SeedList(TestUsers.UserA, "Draft", CommunityListStatus.Draft);
        var favourite = await SeedList(TestUsers.UserB, "By B", CommunityListStatus.Public, favouritedBy: TestUsers.UserA);
        (await HasLists(TestUsers.UserB)).Should().BeFalse();
        (await HasLists(TestUsers.UserA)).Should().BeTrue();

        var written = await SeedList(TestUsers.UserA, "Mine", CommunityListStatus.Public);
        (await HasLists(TestUsers.UserB)).Should().BeTrue();

        async Task<JsonElement> Lists(string? viewer) =>
            await (await Get($"/api/lists/profile/{TestUsers.UserA}", viewer)).Content.ReadFromJsonAsync<JsonElement>();

        var forOthers = await Lists(null);
        forOthers.GetProperty("written").EnumerateArray().Select(l => l.GetProperty("listId").GetInt32()).Should().Equal(written);
        forOthers.GetProperty("favourited").ValueKind.Should().Be(JsonValueKind.Null);

        (await Lists(TestUsers.UserA)).GetProperty("favourited").EnumerateArray().Select(l => l.GetProperty("listId").GetInt32()).Should().Equal(favourite);

        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"favouriteLists":"everyone"}}""");
        (await Lists(TestUsers.UserB)).GetProperty("favourited").GetArrayLength().Should().Be(1);

        await SetPrivacy(TestUsers.UserA, """{"profile":"private"}""");
        (await Get($"/api/lists/profile/{TestUsers.UserA}", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReadableCountsTheCatalogueAtEachCoverageLevel()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);
        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"stats":"everyone"}}""");
        int[] decks = [await SeedDeck("Easy"), await SeedDeck("Medium"), await SeedDeck("Hard")];
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            foreach (var deck in await jitenDb.Decks.Where(d => decks.Contains(d.DeckId)).ToListAsync())
                deck.WordCount = 1000;
            await jitenDb.SaveChangesAsync();
        }

        var before = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        before.GetProperty("readable").ValueKind.Should().Be(JsonValueKind.Null);

        short[] Chunk(params (int DeckId, short BasisPoints)[] values)
        {
            var chunk = new short[1024];
            foreach (var (deckId, basisPoints) in values) chunk[deckId % 1024] = basisPoints;
            return chunk;
        }

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var metadata = await userDb.UserMetadatas.FirstOrDefaultAsync(m => m.UserId == TestUsers.UserA);
            if (metadata == null)
                userDb.UserMetadatas.Add(new UserMetadata { UserId = TestUsers.UserA, CoverageRefreshedAt = DateTime.UtcNow });
            else
                metadata.CoverageRefreshedAt = DateTime.UtcNow;
            userDb.UserCoverageChunks.AddRange(
                new UserCoverageChunk
                {
                    UserId = TestUsers.UserA, Metric = (short)UserCoverageMetric.MatureCoverage, ChunkIndex = decks[0] / 1024,
                    Values = Chunk((decks[0], 9850), (decks[1], 9200), (decks[2], 6000))
                },
                new UserCoverageChunk
                {
                    UserId = TestUsers.UserA, Metric = (short)UserCoverageMetric.YoungCoverage, ChunkIndex = decks[0] / 1024,
                    Values = Chunk((decks[1], 400))
                });
            await userDb.SaveChangesAsync();
        }

        var stats = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        var readable = stats.GetProperty("readable");
        readable.GetProperty("levels").EnumerateArray().Select(l => l.GetInt32()).Should().Equal(80, 85, 90, 95, 98, 99);

        var all = readable.GetProperty("mediaTypes")[0];
        all.GetProperty("mediaType").ValueKind.Should().Be(JsonValueKind.Null);
        all.GetProperty("titles").GetInt32().Should().Be(3);
        all.GetProperty("mature").EnumerateArray().Select(c => c.GetInt32()).Should().Equal(2, 2, 2, 1, 1, 0);
        all.GetProperty("withYoung").EnumerateArray().Select(c => c.GetInt32()).Should().Equal(2, 2, 2, 2, 1, 0);

        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"stats":"everyone"},"hiddenFields":["wordsKnown"]}""");
        var hidden = await (await Get($"/api/user/profile/{TestUsers.UserA}/stats", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        hidden.GetProperty("readable").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task OverviewCarriesTheRollingHeatmapWhereStudyActivityIsVisible()
    {
        await factory.ConfigureProfileAsync(TestUsers.UserA, isPublic: true);

        var overview = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        var heatmap = await (await Get($"/api/user/profile/{TestUsers.UserA}/study-heatmap?rolling=true", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        overview.GetProperty("heatmap").GetRawText().Should().Be(heatmap.GetRawText());

        await SetPrivacy(TestUsers.UserA, """{"profile":"public","sections":{"studyActivity":"onlyMe"}}""");
        var hidden = await (await Get($"/api/user/profile/{TestUsers.UserA}/overview", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        hidden.GetProperty("heatmap").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task DeckPageCarriesTheViewersDetailsFromTheMainEntry()
    {
        var series = await SeedDeck("Series");
        var volume = await SeedDeck("Volume 1", parentDeckId: series);

        async Task<JsonElement> ViewerDetails(int deckId, string? viewer) =>
            (await (await Get($"/api/media-deck/{deckId}/detail", viewer)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("data").GetProperty("viewerDetails");

        (await ViewerDetails(series, TestUsers.UserA)).ValueKind.Should().Be(JsonValueKind.Null);

        await Send(HttpMethod.Patch, $"/api/user/deck-preferences/{series}/details", TestUsers.UserA, new { setScore = true, score = 75, note = "mine" });
        await Send(HttpMethod.Post, $"/api/user/profile/pinned/{series}", TestUsers.UserA);
        var expected = await (await Get($"/api/user/deck-preferences/{volume}/details", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();

        foreach (var deckId in new[] { series, volume })
            (await ViewerDetails(deckId, TestUsers.UserA)).GetRawText().Should().Be(expected.GetRawText());
        (await ViewerDetails(series, TestUsers.UserB)).ValueKind.Should().Be(JsonValueKind.Null);
        (await ViewerDetails(series, null)).ValueKind.Should().Be(JsonValueKind.Null);
    }
}
