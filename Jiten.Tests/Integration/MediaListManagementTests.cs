using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services.ExternalMediaList;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class MediaListManagementTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync()
    {
        factory.ExternalLists.Result = new ExternalListFetchResult([], null);
        factory.ExternalLists.Calls.Clear();
        return factory.ResetDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedDeck(string title, string? linkUrl = null, LinkType linkType = LinkType.Anilist,
                                     int? parentDeckId = null, int deckOrder = 0)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var deck = new Deck { OriginalTitle = title, MediaType = MediaType.Anime, ParentDeckId = parentDeckId, DeckOrder = deckOrder };
        if (linkUrl != null)
            deck.Links.Add(new Link { LinkType = linkType, Url = linkUrl });

        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();
        return deck.DeckId;
    }

    private async Task SeedPreference(int deckId, DeckStatus status = DeckStatus.None, bool isFavourite = false,
                                      bool isIgnored = false, string userId = TestUsers.UserA)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserDeckPreferences.Add(new UserDeckPreference
                                       { UserId = userId, DeckId = deckId, Status = status, IsFavourite = isFavourite, IsIgnored = isIgnored });
        await userDb.SaveChangesAsync();
    }

    private async Task<UserDeckPreference?> GetPreference(int deckId, string userId = TestUsers.UserA)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                              .FirstOrDefaultAsync(userDb.UserDeckPreferences, p => p.UserId == userId && p.DeckId == deckId);
    }

    private static ExternalListEntry Entry(string id, string title, DeckStatus status, DateOnly? finishedAt = null, int? progress = null) =>
        new(id, title, $"https://anilist.co/anime/{id}", status.ToString().ToUpperInvariant(), status, finishedAt, progress);

    // ---- Preview ----

    [Fact]
    public async Task Preview_MatchesLinksAndReportsConflictsAndUnmatched()
    {
        var newDeck = await SeedDeck("New Show", "https://anilist.co/anime/100");
        var conflictDeck = await SeedDeck("Conflict Show", "https://anilist.co/anime/200/some-slug");
        await SeedPreference(conflictDeck, DeckStatus.Planning);

        factory.ExternalLists.Result = new ExternalListFetchResult(
        [
            Entry("100", "New Show", DeckStatus.Ongoing),
            Entry("200", "Conflict Show", DeckStatus.Completed),
            Entry("999", "Missing Show", DeckStatus.Completed),
        ], null);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/preview")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { provider = "anilist", username = "tester" });
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var counts = body.GetProperty("counts");
        counts.GetProperty("total").GetInt32().Should().Be(3);
        counts.GetProperty("matched").GetInt32().Should().Be(2);
        counts.GetProperty("unmatched").GetInt32().Should().Be(1);
        counts.GetProperty("conflicts").GetInt32().Should().Be(1);

        var matched = body.GetProperty("matched").EnumerateArray().ToList();
        var conflictRow = matched.Single(m => m.GetProperty("deckId").GetInt32() == conflictDeck);
        conflictRow.GetProperty("currentStatus").GetInt32().Should().Be((int)DeckStatus.Planning);
        conflictRow.GetProperty("mappedStatus").GetInt32().Should().Be((int)DeckStatus.Completed);

        var newRow = matched.Single(m => m.GetProperty("deckId").GetInt32() == newDeck);
        newRow.GetProperty("currentStatus").ValueKind.Should().Be(JsonValueKind.Null);

        body.GetProperty("unmatched").EnumerateArray().Single().GetProperty("title").GetString().Should().Be("Missing Show");

        factory.ExternalLists.Calls.Should().ContainSingle()
               .Which.Should().Be((ExternalListProvider.Anilist, "tester"));
    }

    [Fact]
    public async Task Preview_IgnoresChildDeckLinks()
    {
        var parent = await SeedDeck("Parent");
        await SeedDeck("Child", "https://anilist.co/anime/300", parentDeckId: parent);

        factory.ExternalLists.Result = new ExternalListFetchResult([Entry("300", "Child", DeckStatus.Completed)], null);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/preview")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { provider = "anilist", username = "tester" });
        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("counts").GetProperty("matched").GetInt32().Should().Be(0);
        body.GetProperty("counts").GetProperty("unmatched").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Preview_MatchesVndbIds()
    {
        var deck = await SeedDeck("Some VN", "https://vndb.org/v17", LinkType.Vndb);

        factory.ExternalLists.Result = new ExternalListFetchResult(
            [new ExternalListEntry("v17", "Some VN", "https://vndb.org/v17", "Finished", DeckStatus.Completed, null)], null);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/preview")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { provider = "vndb", username = "tester" });
        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("counts").GetProperty("matched").GetInt32().Should().Be(1);
        body.GetProperty("matched").EnumerateArray().Single().GetProperty("deckId").GetInt32().Should().Be(deck);
    }

    [Fact]
    public async Task Preview_ReportsProgressAndSubdeckCount()
    {
        var series = await SeedDeck("Series", "https://anilist.co/anime/400");
        await SeedDeck("Episode 1", parentDeckId: series, deckOrder: 1);
        await SeedDeck("Episode 2", parentDeckId: series, deckOrder: 2);
        var movie = await SeedDeck("Movie", "https://anilist.co/anime/401");

        factory.ExternalLists.Result = new ExternalListFetchResult(
        [
            Entry("400", "Series", DeckStatus.Ongoing, progress: 1),
            Entry("401", "Movie", DeckStatus.Completed),
        ], null);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/preview")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { provider = "anilist", username = "tester" });
        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        var matched = body.GetProperty("matched").EnumerateArray().ToList();

        var seriesRow = matched.Single(m => m.GetProperty("deckId").GetInt32() == series);
        seriesRow.GetProperty("progress").GetInt32().Should().Be(1);
        seriesRow.GetProperty("subdeckCount").GetInt32().Should().Be(2);

        var movieRow = matched.Single(m => m.GetProperty("deckId").GetInt32() == movie);
        movieRow.GetProperty("progress").ValueKind.Should().Be(JsonValueKind.Null);
        movieRow.GetProperty("subdeckCount").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("anilist", "https://anilist.co/user/TestUser/", "TestUser")]
    [InlineData("anilist", "https://anilist.co/user/TestUser/animelist", "TestUser")]
    [InlineData("vndb", "https://vndb.org/u1234", "u1234")]
    [InlineData("vndb", "u1234", "u1234")]
    [InlineData("vndb", "testuser", "testuser")]
    public async Task Preview_AcceptsProfileUrlsAndIds(string provider, string input, string expected)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/preview")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { provider, username = input });
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.ExternalLists.Calls.Should().ContainSingle().Which.Username.Should().Be(expected);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task Preview_ProviderErrorSurfacesAsBadRequest()
    {
        factory.ExternalLists.Result = ExternalListFetchResult.Fail("AniList user not found, or their list is private.");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/preview")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { provider = "anilist", username = "nobody" });
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()
                                                                 .Should().Contain("private");
    }

    [Fact]
    public async Task Preview_UnknownProviderRejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/preview")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { provider = "mal", username = "tester" });
        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Apply ----

    [Fact]
    public async Task Apply_AddsUpdatesAndSkips()
    {
        var newDeck = await SeedDeck("New Show");
        var keepDeck = await SeedDeck("Keep Mine");
        await SeedPreference(keepDeck, DeckStatus.Planning);
        var ignoredDeck = await SeedDeck("Ignored Show");
        await SeedPreference(ignoredDeck, isIgnored: true);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/apply")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new
                                       {
                                           overwriteExisting = false,
                                           entries = new[]
                                                     {
                                                         new { deckId = newDeck, status = (int)DeckStatus.Ongoing },
                                                         new { deckId = keepDeck, status = (int)DeckStatus.Completed },
                                                         new { deckId = ignoredDeck, status = (int)DeckStatus.Completed },
                                                     },
                                       });
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("added").GetInt32().Should().Be(1);
        body.GetProperty("skippedExisting").GetInt32().Should().Be(1);
        body.GetProperty("skippedIgnored").GetInt32().Should().Be(1);

        (await GetPreference(newDeck))!.Status.Should().Be(DeckStatus.Ongoing);
        (await GetPreference(keepDeck))!.Status.Should().Be(DeckStatus.Planning);
        (await GetPreference(ignoredDeck))!.Status.Should().Be(DeckStatus.None);
    }

    [Fact]
    public async Task Apply_OverwriteReplacesExistingStatus()
    {
        var deck = await SeedDeck("Show");
        await SeedPreference(deck, DeckStatus.Planning);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/apply")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new
                                       {
                                           overwriteExisting = true,
                                           entries = new[] { new { deckId = deck, status = (int)DeckStatus.Completed } },
                                       });
        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("updated").GetInt32().Should().Be(1);
        (await GetPreference(deck))!.Status.Should().Be(DeckStatus.Completed);
    }

    [Fact]
    public async Task Apply_RejectsChildDecksAndNoneStatus()
    {
        var parent = await SeedDeck("Parent");
        var child = await SeedDeck("Child", parentDeckId: parent);
        var target = await SeedDeck("Target");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/apply")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new
                                       {
                                           overwriteExisting = false,
                                           entries = new[]
                                                     {
                                                         new { deckId = child, status = (int)DeckStatus.Completed },
                                                         new { deckId = target, status = (int)DeckStatus.None },
                                                         new { deckId = 999999, status = (int)DeckStatus.Completed },
                                                     },
                                       });
        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("added").GetInt32().Should().Be(0);
        body.GetProperty("invalid").GetInt32().Should().Be(3);
        (await GetPreference(child)).Should().BeNull();
        (await GetPreference(target)).Should().BeNull();
    }

    // ---- Apply: unit progress ----

    private async Task<JsonElement> ApplyWithProgress(int deckId, DeckStatus status, int? progress, bool overwriteSubdecks = false)
    {
        object entry = progress.HasValue
            ? new { deckId, status = (int)status, progress = progress.Value, overwriteSubdecks }
            : new { deckId, status = (int)status };

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/apply")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { overwriteExisting = true, entries = new[] { entry } });
        return await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Apply_ProgressCompletesLeadingSubdecksInDeckOrder()
    {
        var parent = await SeedDeck("Series");
        var third = await SeedDeck("Episode 3", parentDeckId: parent, deckOrder: 3);
        var second = await SeedDeck("Episode 2", parentDeckId: parent, deckOrder: 2);
        var first = await SeedDeck("Episode 1", parentDeckId: parent, deckOrder: 1);

        var body = await ApplyWithProgress(parent, DeckStatus.Ongoing, 2);

        body.GetProperty("subdecksCompleted").GetInt32().Should().Be(2);
        (await GetPreference(parent))!.Status.Should().Be(DeckStatus.Ongoing);
        (await GetPreference(first))!.Status.Should().Be(DeckStatus.Completed);
        (await GetPreference(second))!.Status.Should().Be(DeckStatus.Completed);
        (await GetPreference(third)).Should().BeNull();
    }

    [Fact]
    public async Task Apply_CompletedParentLeavesSubdecksUntouched()
    {
        var parent = await SeedDeck("Series");
        var child = await SeedDeck("Episode 1", parentDeckId: parent, deckOrder: 1);

        var body = await ApplyWithProgress(parent, DeckStatus.Completed, 1);

        body.GetProperty("subdecksCompleted").GetInt32().Should().Be(0);
        (await GetPreference(parent))!.Status.Should().Be(DeckStatus.Completed);
        (await GetPreference(child)).Should().BeNull();
    }

    [Fact]
    public async Task Apply_WithoutProgressLeavesSubdecksUntouched()
    {
        var parent = await SeedDeck("Series");
        var child = await SeedDeck("Episode 1", parentDeckId: parent, deckOrder: 1);

        var body = await ApplyWithProgress(parent, DeckStatus.Ongoing, null);

        body.GetProperty("subdecksCompleted").GetInt32().Should().Be(0);
        (await GetPreference(child)).Should().BeNull();
    }

    [Fact]
    public async Task Apply_ProgressKeepsExistingSubdeckStatus()
    {
        var parent = await SeedDeck("Series");
        var mine = await SeedDeck("Episode 1", parentDeckId: parent, deckOrder: 1);
        var untracked = await SeedDeck("Episode 2", parentDeckId: parent, deckOrder: 2);
        await SeedPreference(mine, DeckStatus.Dropped);

        var body = await ApplyWithProgress(parent, DeckStatus.Ongoing, 2);

        body.GetProperty("subdecksCompleted").GetInt32().Should().Be(1);
        (await GetPreference(mine))!.Status.Should().Be(DeckStatus.Dropped);
        (await GetPreference(untracked))!.Status.Should().Be(DeckStatus.Completed);
    }

    [Fact]
    public async Task Apply_ProgressOverwritesExistingSubdeckStatus()
    {
        var parent = await SeedDeck("Series");
        var mine = await SeedDeck("Episode 1", parentDeckId: parent, deckOrder: 1);
        await SeedPreference(mine, DeckStatus.Dropped);

        var body = await ApplyWithProgress(parent, DeckStatus.Ongoing, 1, overwriteSubdecks: true);

        body.GetProperty("subdecksCompleted").GetInt32().Should().Be(1);
        (await GetPreference(mine))!.Status.Should().Be(DeckStatus.Completed);
    }

    [Fact]
    public async Task Apply_ProgressCappedAtSubdeckCount()
    {
        var parent = await SeedDeck("Series");
        var first = await SeedDeck("Episode 1", parentDeckId: parent, deckOrder: 1);
        var second = await SeedDeck("Episode 2", parentDeckId: parent, deckOrder: 2);

        var body = await ApplyWithProgress(parent, DeckStatus.Ongoing, 99);

        body.GetProperty("subdecksCompleted").GetInt32().Should().Be(2);
        (await GetPreference(first))!.Status.Should().Be(DeckStatus.Completed);
        (await GetPreference(second))!.Status.Should().Be(DeckStatus.Completed);
    }

    [Fact]
    public async Task Apply_ProgressSkipsIgnoredSubdecks()
    {
        var parent = await SeedDeck("Series");
        var ignored = await SeedDeck("Episode 1", parentDeckId: parent, deckOrder: 1);
        var normal = await SeedDeck("Episode 2", parentDeckId: parent, deckOrder: 2);
        await SeedPreference(ignored, isIgnored: true);

        var body = await ApplyWithProgress(parent, DeckStatus.Ongoing, 2, overwriteSubdecks: true);

        body.GetProperty("subdecksCompleted").GetInt32().Should().Be(1);
        (await GetPreference(ignored))!.Status.Should().Be(DeckStatus.None);
        (await GetPreference(normal))!.Status.Should().Be(DeckStatus.Completed);
    }

    // ---- Bulk deck preferences ----

    [Fact]
    public async Task Bulk_SetStatus()
    {
        var a = await SeedDeck("A");
        var b = await SeedDeck("B");
        await SeedPreference(a, DeckStatus.Ongoing);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/deck-preferences/bulk")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { deckIds = new[] { a, b }, status = (int)DeckStatus.Completed });
        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("affected").GetInt32().Should().Be(2);
        (await GetPreference(a))!.Status.Should().Be(DeckStatus.Completed);
        (await GetPreference(b))!.Status.Should().Be(DeckStatus.Completed);
    }

    [Fact]
    public async Task Bulk_FavouriteSkipsIgnoredDecks()
    {
        var normal = await SeedDeck("Normal");
        await SeedPreference(normal, DeckStatus.Ongoing);
        var ignored = await SeedDeck("Ignored");
        await SeedPreference(ignored, isIgnored: true);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/deck-preferences/bulk")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { deckIds = new[] { normal, ignored }, isFavourite = true });
        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("affected").GetInt32().Should().Be(1);
        body.GetProperty("skipped").GetInt32().Should().Be(1);
        (await GetPreference(normal))!.IsFavourite.Should().BeTrue();
        (await GetPreference(ignored))!.IsFavourite.Should().BeFalse();
    }

    [Fact]
    public async Task Bulk_RemoveClearsStatusButKeepsFavourites()
    {
        var plain = await SeedDeck("Plain");
        await SeedPreference(plain, DeckStatus.Completed);
        var favourite = await SeedDeck("Favourite");
        await SeedPreference(favourite, DeckStatus.Completed, isFavourite: true);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/deck-preferences/bulk")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { deckIds = new[] { plain, favourite }, remove = true });
        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("affected").GetInt32().Should().Be(2);
        (await GetPreference(plain)).Should().BeNull();

        var kept = await GetPreference(favourite);
        kept!.Status.Should().Be(DeckStatus.None);
        kept.IsFavourite.Should().BeTrue();
    }

    [Fact]
    public async Task Bulk_RequiresExactlyOneOperation()
    {
        var a = await SeedDeck("A");

        var none = new HttpRequestMessage(HttpMethod.Post, "/api/user/deck-preferences/bulk")
                   .WithUser(TestUsers.UserA)
                   .WithJsonContent(new { deckIds = new[] { a } });
        (await _client.SendAsync(none)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var two = new HttpRequestMessage(HttpMethod.Post, "/api/user/deck-preferences/bulk")
                  .WithUser(TestUsers.UserA)
                  .WithJsonContent(new { deckIds = new[] { a }, status = (int)DeckStatus.Completed, remove = true });
        (await _client.SendAsync(two)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Bulk_OnlyTouchesOwnPreferences()
    {
        var deck = await SeedDeck("Shared");
        await SeedPreference(deck, DeckStatus.Ongoing, userId: TestUsers.UserB);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/deck-preferences/bulk")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { deckIds = new[] { deck }, remove = true });
        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("affected").GetInt32().Should().Be(0);
        (await GetPreference(deck, TestUsers.UserB))!.Status.Should().Be(DeckStatus.Ongoing);
    }

    // ---- Export ----

    [Fact]
    public async Task Export_CsvHasBomHeaderAndRows()
    {
        var deck = await SeedDeck("Show, with comma", "https://anilist.co/anime/1");
        await SeedPreference(deck, DeckStatus.Completed, isFavourite: true);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/user/media-list/export?format=csv")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);

        var text = System.Text.Encoding.UTF8.GetString(bytes.Skip(3).ToArray());
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].TrimEnd().Should().Be("DeckId,OriginalTitle,RomajiTitle,EnglishTitle,MediaType,Status,Progress,IsFavourite,JitenUrl,ExternalLinks");
        lines[1].Should().Contain("\"Show, with comma\"").And.Contain("Completed").And.Contain("True")
                .And.Contain($"https://jiten.moe/decks/media/{deck}");
    }

    [Fact]
    public async Task Export_JsonListsEntries()
    {
        var deck = await SeedDeck("Show");
        await SeedPreference(deck, DeckStatus.Ongoing);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/user/media-list/export?format=json")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        items.Should().HaveCount(1);
        items![0].GetProperty("deckId").GetInt32().Should().Be(deck);
        items[0].GetProperty("status").GetString().Should().Be("Ongoing");
    }
}
