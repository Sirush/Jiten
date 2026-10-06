using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services.ExternalMediaList;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class MediaListEntryTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private async Task<int> SeedDeck(string title, int? parentDeckId = null)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var deck = new Deck { OriginalTitle = title, MediaType = MediaType.VisualNovel, ParentDeckId = parentDeckId, CharacterCount = 1000 };
        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();
        return deck.DeckId;
    }

    private async Task<List<UserMediaListEntry>> Entries(int deckId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserMediaListEntries.AsNoTracking().Where(r => r.UserId == TestUsers.UserA && r.DeckId == deckId).OrderBy(r => r.Id).ToListAsync();
    }

    private async Task<UserDeckPreference?> Preference(int deckId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserDeckPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == TestUsers.UserA && p.DeckId == deckId);
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url).WithUser(TestUsers.UserA);
        if (body != null)
            request.WithJsonContent(body);
        return _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SetStatus(int deckId, DeckStatus status, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["status"] = status };
        if (extra != null)
            foreach (var property in extra.GetType().GetProperties())
                body[property.Name] = property.GetValue(extra);

        var response = await Send(HttpMethod.Post, $"/api/user/deck-preferences/{deckId}/status", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return response;
    }

    [Theory]
    [InlineData("2024-05-12")]
    [InlineData(null)]
    public async Task Completing_RecordsTheChosenFinishDateOrLeavesItUnknown(string? date)
    {
        var deckId = await SeedDeck("VN");

        var response = await SetStatus(deckId, DeckStatus.Completed, date == null ? new { dateUnknown = true } : (object)new { date });

        var entries = await Entries(deckId);
        entries.Should().ContainSingle();
        entries[0].State.Should().Be(MediaListEntryState.Completed);
        entries[0].FinishedOn.Should().Be(date == null ? (DateOnly?)null : DateOnly.Parse(date));
        (await Preference(deckId))!.CurrentEntryId.Should().Be(entries[0].Id);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("listEntry").GetProperty("finishedOn").GetString().Should().Be(date);
    }

    [Fact]
    public async Task Completing_DefaultsToToday()
    {
        var deckId = await SeedDeck("VN");

        await SetStatus(deckId, DeckStatus.Ongoing);
        await SetStatus(deckId, DeckStatus.Completed);

        var entry = (await Entries(deckId)).Single();
        entry.StartedOn.Should().Be(Today);
        entry.FinishedOn.Should().Be(Today);
    }

    [Fact]
    public async Task FutureDate_IsRejected()
    {
        var deckId = await SeedDeck("VN");

        var response = await Send(HttpMethod.Post, $"/api/user/deck-preferences/{deckId}/status",
                                  new { status = DeckStatus.Completed, date = Today.AddDays(5).ToString("yyyy-MM-dd") });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Entries(deckId)).Should().BeEmpty();
    }

    [Fact]
    public async Task LeavingCompleted_ReopensTheSameEntry()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Completed, new { date = "2024-05-12" });

        await SetStatus(deckId, DeckStatus.Ongoing);

        var entry = (await Entries(deckId)).Single();
        entry.State.Should().Be(MediaListEntryState.InProgress);
        entry.FinishedOn.Should().BeNull();
    }

    [Fact]
    public async Task NewEntry_StartsAnotherPassAndKeepsTheCompletion()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Completed, new { date = "2024-05-12" });

        await SetStatus(deckId, DeckStatus.Ongoing, new { newEntry = true, date = "2024-12-20" });
        await SetStatus(deckId, DeckStatus.Completed, new { date = "2025-01-03" });

        var entries = await Entries(deckId);
        entries.Should().HaveCount(2);
        entries.Select(r => r.FinishedOn).Should().Equal(new DateOnly(2024, 5, 12), new DateOnly(2025, 1, 3));
        entries.Should().OnlyContain(r => r.State == MediaListEntryState.Completed);
    }

    [Fact]
    public async Task BackToPlanning_DropsAnEmptyEntryButKeepsOneWithCharacters()
    {
        var emptyDeck = await SeedDeck("Empty");
        var countedDeck = await SeedDeck("Counted");
        await SetStatus(emptyDeck, DeckStatus.Ongoing);
        await SetStatus(countedDeck, DeckStatus.Ongoing);
        var counted = (await Entries(countedDeck)).Single();
        (await Send(HttpMethod.Put, $"/api/user/media-list/entries/{countedDeck}/{counted.Id}",
                    new { startedOn = counted.StartedOn, charactersRead = 300 })).StatusCode.Should().Be(HttpStatusCode.OK);

        await SetStatus(emptyDeck, DeckStatus.Planning);
        await SetStatus(countedDeck, DeckStatus.Planning);

        (await Entries(emptyDeck)).Should().BeEmpty();
        var kept = (await Entries(countedDeck)).Single();
        kept.State.Should().Be(MediaListEntryState.Dropped);
        kept.CharactersRead.Should().Be(300);
    }

    [Fact]
    public async Task RemovingFromTheList_DeletesTheHistory()
    {
        var cleared = await SeedDeck("Cleared");
        var bulkRemoved = await SeedDeck("Bulk removed");
        await SetStatus(cleared, DeckStatus.Completed);
        await SetStatus(bulkRemoved, DeckStatus.Completed);

        await SetStatus(cleared, DeckStatus.None);
        (await Send(HttpMethod.Post, "/api/user/deck-preferences/bulk", new { deckIds = new[] { bulkRemoved }, remove = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await Entries(cleared)).Should().BeEmpty();
        (await Preference(cleared))!.CurrentEntryId.Should().BeNull();
        (await Entries(bulkRemoved)).Should().BeEmpty();
    }

    [Fact]
    public async Task BulkComplete_LeavesTheDateUnknown()
    {
        var deckId = await SeedDeck("VN");

        await Send(HttpMethod.Post, "/api/user/deck-preferences/bulk", new { deckIds = new[] { deckId }, status = DeckStatus.Completed });

        var entry = (await Entries(deckId)).Single();
        entry.State.Should().Be(MediaListEntryState.Completed);
        entry.FinishedOn.Should().BeNull();
    }

    [Fact]
    public async Task PastEntry_OnlyBecomesCurrentOnATitleWithNoStatus()
    {
        var tracked = await SeedDeck("Tracked");
        var untracked = await SeedDeck("Untracked");
        await SetStatus(tracked, DeckStatus.Ongoing);
        var current = (await Entries(tracked)).Single();

        foreach (var deckId in new[] { tracked, untracked })
            (await Send(HttpMethod.Post, $"/api/user/media-list/entries/{deckId}", new { state = MediaListEntryState.Completed, finishedOn = "2020-02-02" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

        var trackedPreference = (await Preference(tracked))!;
        trackedPreference.CurrentEntryId.Should().Be(current.Id);
        trackedPreference.Status.Should().Be(DeckStatus.Ongoing);
        var untrackedPreference = (await Preference(untracked))!;
        untrackedPreference.Status.Should().Be(DeckStatus.Completed);
        untrackedPreference.CurrentEntryId.Should().Be((await Entries(untracked)).Single().Id);
    }

    [Fact]
    public async Task NewEntry_IsRefusedWhileOneIsInProgress()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Ongoing);

        var response = await Send(HttpMethod.Post, $"/api/user/media-list/entries/{deckId}", new { state = MediaListEntryState.InProgress });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CharacterCount_AcceptedOnSeriesVolumesAndStandaloneTitles()
    {
        var parentId = await SeedDeck("Series");
        var childId = await SeedDeck("Volume 1", parentId);
        var standaloneId = await SeedDeck("VN");
        await SetStatus(childId, DeckStatus.Completed);
        await SetStatus(parentId, DeckStatus.Completed);
        await SetStatus(standaloneId, DeckStatus.Completed);

        foreach (var deckId in new[] { parentId, childId, standaloneId })
        {
            var entry = (await Entries(deckId)).Single();
            (await Send(HttpMethod.Put, $"/api/user/media-list/entries/{deckId}/{entry.Id}", new { charactersRead = 0 }))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Send(HttpMethod.Put, $"/api/user/media-list/entries/{deckId}/{entry.Id}", new { charactersRead = 500 }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await Entries(deckId)).Single().CharactersRead.Should().Be(500);
        }
    }

    [Fact]
    public async Task FinishBeforeStart_IsRejected()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Completed);
        var entry = (await Entries(deckId)).Single();

        var response = await Send(HttpMethod.Put, $"/api/user/media-list/entries/{deckId}/{entry.Id}",
                                  new { startedOn = "2024-06-01", finishedOn = "2024-05-01" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OtherUsersEntriesAreNotEditable()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Completed);
        var entry = (await Entries(deckId)).Single();

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/user/media-list/entries/{deckId}/{entry.Id}").WithUser(TestUsers.UserB);
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Entries(deckId)).Should().ContainSingle();
    }

    [Fact]
    public async Task DeletingTheLastCompletion_FollowsTheLatestRemainingEntryOrLeavesTheList()
    {
        var only = await SeedDeck("Only");
        await SetStatus(only, DeckStatus.Completed);
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Ongoing);
        await SetStatus(deckId, DeckStatus.Dropped);
        await SetStatus(deckId, DeckStatus.Ongoing, new { newEntry = true });
        await SetStatus(deckId, DeckStatus.Completed);
        var entries = await Entries(deckId);
        entries.Select(r => r.State).Should().Equal(MediaListEntryState.Dropped, MediaListEntryState.Completed);

        var body = await (await Send(HttpMethod.Delete, $"/api/user/media-list/entries/{only}/{(await Entries(only)).Single().Id}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        await Send(HttpMethod.Delete, $"/api/user/media-list/entries/{deckId}/{entries[1].Id}");

        (await Preference(only)).Should().BeNull();
        body.GetProperty("status").GetInt32().Should().Be((int)DeckStatus.None);
        var preference = (await Preference(deckId))!;
        preference.Status.Should().Be(DeckStatus.Dropped);
        preference.CurrentEntryId.Should().Be(entries[0].Id);
    }

    [Fact]
    public async Task Import_StoresSourceDatesAndRepeats_Once()
    {
        var deckId = await SeedDeck("VN");
        var entry = new
                    {
                        deckId, status = DeckStatus.Completed, startedOn = "2023-01-02", finishedOn = "2023-02-03", repeatCount = 2,
                        charactersRead = 700
                    };

        (await Send(HttpMethod.Post, "/api/user/media-list/import/apply", new { entries = new[] { entry }, overwriteExisting = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Post, "/api/user/media-list/import/apply", new { entries = new[] { entry }, overwriteExisting = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await Entries(deckId);
        entries.Should().HaveCount(3);
        entries.Should().OnlyContain(r => r.State == MediaListEntryState.Completed);
        var currentReadId = (await Preference(deckId))!.CurrentEntryId;
        var current = entries.Single(r => r.Id == currentReadId);
        current.StartedOn.Should().Be(new DateOnly(2023, 1, 2));
        current.FinishedOn.Should().Be(new DateOnly(2023, 2, 3));
        current.CharactersRead.Should().Be(700);
    }

    [Fact]
    public async Task Export_CarriesReadingColumns()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Completed, new { date = "2024-05-12" });

        var csv = await (await Send(HttpMethod.Get, "/api/user/media-list/export?format=csv")).Content.ReadAsStringAsync();
        var lines = csv.TrimStart('﻿').Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines[0].Trim().Should().EndWith("ExternalLinks,StartedOn,FinishedOn,TimesCompleted,CharactersRead");
        lines[1].Trim().Should().EndWith(",,2024-05-12,1,");
    }

    private async Task Age(int deckId, int days)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var then = DateTime.UtcNow.AddDays(-days);
        await userDb.UserMediaListEntries.Where(r => r.UserId == TestUsers.UserA && r.DeckId == deckId)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.UpdatedAt, then));
        await userDb.UserDeckPreferences.Where(p => p.UserId == TestUsers.UserA && p.DeckId == deckId)
                    .ExecuteUpdateAsync(u => u.SetProperty(p => p.UpdatedAt, then));
    }

    private async Task<List<int>> StaleDeckIds()
    {
        var body = await (await Send(HttpMethod.Get, "/api/user/media-list/stale")).Content.ReadFromJsonAsync<JsonElement>();
        return body.EnumerateArray().Select(e => e.GetProperty("deckId").GetInt32()).ToList();
    }

    [Fact]
    public async Task Stale_ListsUntouchedTitlesInProgress_UntilProgressIsLogged()
    {
        var untouched = await SeedDeck("Untouched");
        var recent = await SeedDeck("Recent");
        var series = await SeedDeck("Series");
        await SeedDeck("Volume 1", parentDeckId: series);
        var finished = await SeedDeck("Finished");

        foreach (var deckId in new[] { untouched, recent, series })
            await SetStatus(deckId, DeckStatus.Ongoing);
        await SetStatus(finished, DeckStatus.Completed);
        foreach (var deckId in new[] { untouched, series, finished })
            await Age(deckId, 45);

        (await StaleDeckIds()).Should().BeEquivalentTo([untouched, series]);

        var entry = (await Entries(untouched)).Single();
        var summary = (await (await Send(HttpMethod.Get, $"/api/user/media-list/entries/{untouched}")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("summary");
        summary.GetProperty("entryId").GetInt64().Should().Be(entry.Id);

        (await Send(HttpMethod.Put, $"/api/user/media-list/entries/{untouched}/{entry.Id}", new { startedOn = (string?)null, finishedOn = (string?)null, charactersRead = 300 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await StaleDeckIds()).Should().Equal(series);
    }

    [Fact]
    public async Task Stale_SeriesStaysFreshWhileItsVolumesChange()
    {
        var (series, volumes) = await SeedSeries(3);
        await SetStatus(series, DeckStatus.Ongoing);
        await Age(series, 45);
        await SetUnits(series, 1);

        (await StaleDeckIds()).Should().BeEmpty();

        await Age(volumes[0], 45);
        (await StaleDeckIds()).Should().Equal(series);
    }

    [Fact]
    public async Task Stale_AStaleVolumeIsAskedAboutInsteadOfItsSeries()
    {
        var (series, volumes) = await SeedSeries(3);
        await SetStatus(volumes[1], DeckStatus.Ongoing);
        await SetStatus(series, DeckStatus.Ongoing);
        await Age(volumes[1], 45);
        await Age(series, 45);

        (await StaleDeckIds()).Should().Equal(volumes[1]);
    }

    [Fact]
    public async Task StillGoing_RestartsTheMonthOfATitleInProgress()
    {
        var deckId = await SeedDeck("Long novel");
        var finished = await SeedDeck("Finished novel");
        await SetStatus(deckId, DeckStatus.Ongoing);
        await SetStatus(finished, DeckStatus.Completed);
        await Age(deckId, 45);

        (await Send(HttpMethod.Post, $"/api/user/media-list/stale/{deckId}/still-going")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Post, $"/api/user/media-list/stale/{finished}/still-going")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await StaleDeckIds()).Should().BeEmpty();
        (await Entries(deckId)).Single().State.Should().Be(MediaListEntryState.InProgress);
    }

    private async Task<(int Series, List<int> Volumes)> SeedSeries(int volumes, MediaType mediaType = MediaType.Novel)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var series = new Deck { OriginalTitle = "Series", MediaType = mediaType, CharacterCount = 1000 * volumes };
        jitenDb.Decks.Add(series);
        await jitenDb.SaveChangesAsync();

        var children = Enumerable.Range(1, volumes)
                                 .Select(i => new Deck
                                              {
                                                  OriginalTitle = $"Volume {i}", MediaType = mediaType, ParentDeckId = series.DeckId, DeckOrder = i,
                                                  CharacterCount = 1000
                                              })
                                 .ToList();
        jitenDb.Decks.AddRange(children);
        await jitenDb.SaveChangesAsync();
        return (series.DeckId, children.Select(c => c.DeckId).ToList());
    }

    private async Task<JsonElement> SetUnits(int deckId, int completed, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await Send(HttpMethod.Post, $"/api/user/media-list/units/{deckId}", new { completed });
        response.StatusCode.Should().Be(expected);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Units_CompleteTheEarliestUnfinishedVolumesAndOpenTheSeries()
    {
        var (series, volumes) = await SeedSeries(4);
        await SetStatus(volumes[1], DeckStatus.Completed);

        var body = await SetUnits(series, 3);

        body.GetProperty("volumes").EnumerateArray().Select(v => v.GetProperty("deckId").GetInt32()).Should().Equal(volumes[0], volumes[2]);
        body.GetProperty("status").GetInt32().Should().Be((int)DeckStatus.Ongoing);
        body.GetProperty("listEntry").GetProperty("completedUnits").GetInt32().Should().Be(3);
        body.GetProperty("listEntry").GetProperty("unitCount").GetInt32().Should().Be(4);
        body.GetProperty("allChildrenCompleted").GetBoolean().Should().BeFalse();
        (await Preference(volumes[0]))!.Status.Should().Be(DeckStatus.Completed);
        (await Preference(volumes[2]))!.Status.Should().Be(DeckStatus.Completed);
        (await Preference(volumes[3])).Should().BeNull();
        (await Entries(volumes[2])).Single().FinishedOn.Should().Be(Today);
    }

    [Fact]
    public async Task Units_LoweringRemovesBareCompletionsAndReopensOnesWithHistory()
    {
        var (series, volumes) = await SeedSeries(4);
        (await SetUnits(series, 4)).GetProperty("allChildrenCompleted").GetBoolean().Should().BeTrue();
        await SetStatus(series, DeckStatus.Completed);
        var withStart = (await Entries(volumes[3])).Single();
        await Send(HttpMethod.Put, $"/api/user/media-list/entries/{volumes[3]}/{withStart.Id}",
                   new { startedOn = "2024-01-01", finishedOn = withStart.FinishedOn, charactersRead = (int?)null });

        var body = await SetUnits(series, 1);

        body.GetProperty("status").GetInt32().Should().Be((int)DeckStatus.Ongoing);
        body.GetProperty("listEntry").GetProperty("completedUnits").GetInt32().Should().Be(1);
        (await Preference(volumes[0]))!.Status.Should().Be(DeckStatus.Completed);
        (await Preference(volumes[1])).Should().BeNull();
        (await Preference(volumes[2])).Should().BeNull();
        (await Entries(volumes[2])).Should().BeEmpty();
        (await Preference(volumes[3]))!.Status.Should().Be(DeckStatus.Ongoing);
        (await Entries(volumes[3])).Single().StartedOn.Should().Be(new DateOnly(2024, 1, 1));
    }

    [Fact]
    public async Task Units_LoweringPastAVolumeBeingReadAgainIsRefused()
    {
        var (series, volumes) = await SeedSeries(3);
        await SetStatus(series, DeckStatus.Ongoing);
        await SetUnits(series, 1);
        await SetStatus(volumes[0], DeckStatus.Ongoing, new { newEntry = true });

        var body = await SetUnits(series, 0, HttpStatusCode.BadRequest);

        body.GetProperty("message").GetString().Should().Contain("earlier read");
        (await Entries(volumes[0])).Select(r => r.State).Should().Equal(MediaListEntryState.Completed, MediaListEntryState.InProgress);
    }

    [Fact]
    public async Task Units_RejectedOutsideASeriesReadInOrder()
    {
        var (unordered, _) = await SeedSeries(2, MediaType.VisualNovel);
        var (series, volumes) = await SeedSeries(2);

        await SetUnits(unordered, 1, HttpStatusCode.BadRequest);
        await SetUnits(series, 3, HttpStatusCode.BadRequest);
        await SetUnits(volumes[0], 1, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SeriesSummary_AddsUpItsVolumesUntilTheSeriesHasItsOwnCount()
    {
        var (series, volumes) = await SeedSeries(3);
        await SetStatus(series, DeckStatus.Ongoing);
        await SetUnits(series, 1);
        await SetStatus(volumes[1], DeckStatus.Ongoing);
        var reading = (await Entries(volumes[1])).Single();
        await Send(HttpMethod.Put, $"/api/user/media-list/entries/{volumes[1]}/{reading.Id}", new { charactersRead = 400 });

        async Task<JsonElement> Summary() =>
            (await (await Send(HttpMethod.Get, $"/api/user/media-list/entries/{series}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("summary");

        (await Summary()).GetProperty("volumeCharacters").GetInt32().Should().Be(1400);

        await SetStatus(series, DeckStatus.Completed);
        (await Summary()).GetProperty("volumeCharacters").GetInt32().Should().Be(3000);
    }

    private async Task LogCharacters(int deckId, int characters)
    {
        var entry = (await Entries(deckId)).Last();
        (await Send(HttpMethod.Put, $"/api/user/media-list/entries/{deckId}/{entry.Id}",
                    new { startedOn = entry.StartedOn, finishedOn = entry.FinishedOn, charactersRead = characters }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Completing_DropsTheLoggedProgressUnlessItWentPastTheDeck()
    {
        var partial = await SeedDeck("Partial");
        var longer = await SeedDeck("Longer");
        foreach (var (deckId, characters) in new[] { (partial, 300), (longer, 1200) })
        {
            await SetStatus(deckId, DeckStatus.Ongoing);
            await LogCharacters(deckId, characters);
            await SetStatus(deckId, DeckStatus.Completed);
        }

        (await Entries(partial)).Single().CharactersRead.Should().BeNull();
        (await Entries(longer)).Single().CharactersRead.Should().Be(1200);

        await SetStatus(partial, DeckStatus.Ongoing);
        (await Entries(partial)).Single().CharactersRead.Should().BeNull();
    }

    [Fact]
    public async Task PlanningAReread_StartsANewPassInsteadOfReopeningTheCompletion()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Completed, new { date = "2024-05-12" });

        await SetStatus(deckId, DeckStatus.Planning);
        await SetStatus(deckId, DeckStatus.Ongoing);

        var entries = await Entries(deckId);
        entries.Select(r => r.State).Should().Equal(MediaListEntryState.Completed, MediaListEntryState.InProgress);
        entries[0].FinishedOn.Should().Be(new DateOnly(2024, 5, 12));
        (await Preference(deckId))!.CurrentEntryId.Should().Be(entries[1].Id);
    }

    [Fact]
    public async Task Units_CountAVolumeBeingReadAgainAsDone()
    {
        var (series, volumes) = await SeedSeries(3);
        await SetUnits(series, 1);
        await SetStatus(volumes[0], DeckStatus.Ongoing, new { newEntry = true });

        var body = await SetUnits(series, 2);

        body.GetProperty("listEntry").GetProperty("completedUnits").GetInt32().Should().Be(2);
        (await Entries(volumes[0])).Select(r => r.State).Should().Equal(MediaListEntryState.Completed, MediaListEntryState.InProgress);
        (await Preference(volumes[1]))!.Status.Should().Be(DeckStatus.Completed);
        (await Preference(volumes[2])).Should().BeNull();
    }

    [Fact]
    public async Task CompletingTheLastVolume_OffersTheSeriesEvenWhenItIsAlreadyOngoing()
    {
        var (series, volumes) = await SeedSeries(2);
        await SetStatus(volumes[0], DeckStatus.Completed);
        await SetStatus(volumes[0], DeckStatus.Ongoing, new { newEntry = true });
        (await Preference(series))!.Status.Should().Be(DeckStatus.Ongoing);

        var body = await (await SetStatus(volumes[1], DeckStatus.Completed)).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("allChildrenCompleted").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task BackToPlanning_KeepsARealPassToResumeOrRestart(bool newEntry, int expectedEntries)
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Ongoing, new { date = "2024-02-01" });
        await LogCharacters(deckId, 300);

        await SetStatus(deckId, DeckStatus.Planning);
        (await Entries(deckId)).Single().State.Should().Be(MediaListEntryState.Dropped);

        await SetStatus(deckId, DeckStatus.Ongoing, new { newEntry });

        var entries = await Entries(deckId);
        entries.Should().HaveCount(expectedEntries);
        entries.Last().State.Should().Be(MediaListEntryState.InProgress);
        entries[0].StartedOn.Should().Be(new DateOnly(2024, 2, 1));
    }

    private async Task MakeListPublic()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.Users.Where(u => u.Id == TestUsers.UserA)
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.NormalizedUserName, TestUsers.UserA.ToUpperInvariant()));
        userDb.UserProfiles.Add(new UserProfile { UserId = TestUsers.UserA, IsPublic = true, IsMediaListPublic = true });
        userDb.UserAccomplishments.Add(new UserAccomplishment
                                       {
                                           UserId = TestUsers.UserA, MediaType = null, UnfinishedCharacterCount = 500, TotalCharacterCount = 3000,
                                           CompletedDeckCount = 1, CompletedUnitCount = 1
                                       });
        userDb.UserAccomplishments.Add(new UserAccomplishment { UserId = TestUsers.UserA, MediaType = MediaType.Novel, UnfinishedCharacterCount = 200 });
        await userDb.SaveChangesAsync();
    }

    [Fact]
    public async Task PublicList_KeepsEntriesAndUnfinishedCountsPrivate()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Completed, new { date = "2024-05-12" });
        await MakeListPublic();

        async Task<JsonElement> Get(string url, string? user)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (user != null)
                request.WithUser(user);
            var response = await _client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var listUrl = $"/api/user/profile/{TestUsers.UserA}/media-list";
        var accomplishmentsUrl = $"/api/user/profile/{TestUsers.UserA}/accomplishments";
        var unfinishedOnlyUrl = $"/api/user/user/{TestUsers.UserA}/accomplishments/{MediaType.Novel}";
        foreach (var viewer in new[] { null, TestUsers.UserB })
        {
            var deck = (await Get(listUrl, viewer)).EnumerateArray().Single();
            (deck.TryGetProperty("listEntry", out var listEntry) ? listEntry.ValueKind : JsonValueKind.Null).Should().Be(JsonValueKind.Null);
            var totals = (await Get(accomplishmentsUrl, viewer)).EnumerateArray().Single();
            totals.GetProperty("unfinishedCharacterCount").GetInt64().Should().Be(0);
            totals.GetProperty("totalCharacterCount").GetInt64().Should().Be(3000);

            var unfinishedOnly = new HttpRequestMessage(HttpMethod.Get, unfinishedOnlyUrl);
            if (viewer != null)
                unfinishedOnly.WithUser(viewer);
            (await _client.SendAsync(unfinishedOnly)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        (await Get(listUrl, TestUsers.UserA)).EnumerateArray().Single().GetProperty("listEntry").GetProperty("finishedOn").GetString()
                                            .Should().Be("2024-05-12");
        var ownTotals = (await Get(accomplishmentsUrl, TestUsers.UserA)).EnumerateArray().ToList();
        ownTotals.Should().HaveCount(2);
        ownTotals.Single(t => t.GetProperty("mediaType").ValueKind == JsonValueKind.Null).GetProperty("unfinishedCharacterCount").GetInt64().Should().Be(500);
        (await Get(unfinishedOnlyUrl, TestUsers.UserA)).GetProperty("unfinishedCharacterCount").GetInt64().Should().Be(200);
    }

    [Fact]
    public async Task DeletingTheCurrentEntry_MovesToAnotherEntryTheStatusDescribes_NeverToAPastOne()
    {
        var reading = await SeedDeck("Reading again");
        var finished = await SeedDeck("Finished twice");
        foreach (var deckId in new[] { reading, finished })
        {
            await SetStatus(deckId, DeckStatus.Completed, new { date = "2024-05-12" });
            await SetStatus(deckId, DeckStatus.Ongoing, new { newEntry = true });
        }
        await SetStatus(finished, DeckStatus.Completed, new { date = "2025-01-03" });

        var body = await (await Send(HttpMethod.Delete, $"/api/user/media-list/entries/{reading}/{(await Entries(reading)).Last().Id}"))
            .Content.ReadFromJsonAsync<JsonElement>();
        var finishedEntries = await Entries(finished);
        await Send(HttpMethod.Delete, $"/api/user/media-list/entries/{finished}/{finishedEntries[1].Id}");

        (await Preference(reading))!.CurrentEntryId.Should().BeNull();
        var summary = body.GetProperty("summary");
        summary.GetProperty("entryId").ValueKind.Should().Be(JsonValueKind.Null);
        summary.GetProperty("finishedOn").GetString().Should().Be("2024-05-12");
        (await Preference(finished))!.CurrentEntryId.Should().Be(finishedEntries[0].Id);
    }

    [Fact]
    public async Task PastEntryOnAVolume_OpensItsSeries()
    {
        var (series, volumes) = await SeedSeries(2);

        var body = await (await Send(HttpMethod.Post, $"/api/user/media-list/entries/{volumes[0]}", new { state = MediaListEntryState.Completed }))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("status").GetInt32().Should().Be((int)DeckStatus.Completed);
        body.GetProperty("parentDeckId").GetInt32().Should().Be(series);
        (await Preference(series))!.Status.Should().Be(DeckStatus.Ongoing);
    }

    [Fact]
    public async Task Import_HistoryKeepsTheFlaggedCurrentEntry()
    {
        var deckId = await SeedDeck("VN");
        var entry = new
                    {
                        deckId, status = DeckStatus.Ongoing,
                        history = new object[]
                                {
                                    new { state = MediaListEntryState.InProgress, isCurrent = true },
                                    new { state = MediaListEntryState.Completed, finishedOn = "2021-03-04" },
                                }
                    };

        (await Send(HttpMethod.Post, "/api/user/media-list/import/apply", new { entries = new[] { entry } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var entries = await Entries(deckId);
        entries.Should().HaveCount(2);
        (await Preference(deckId))!.CurrentEntryId.Should().Be(entries.Single(r => r.State == MediaListEntryState.InProgress).Id);
    }

    [Fact]
    public async Task BulkOngoing_KeepsTheCompletion()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Completed, new { date = "2024-05-12" });

        (await Send(HttpMethod.Post, "/api/user/deck-preferences/bulk", new { deckIds = new[] { deckId }, status = DeckStatus.Ongoing }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await Entries(deckId)).Select(r => r.State).Should().Equal(MediaListEntryState.Completed, MediaListEntryState.InProgress);
    }

    [Fact]
    public async Task Units_RaisingThenLoweringLeavesOlderCompletionsAlone()
    {
        var (series, volumes) = await SeedSeries(4);
        foreach (var volume in new[] { volumes[0], volumes[1], volumes[3] })
        {
            await SetStatus(volume, DeckStatus.Completed, new { dateUnknown = true });
        }

        await SetStatus(volumes[2], DeckStatus.Ongoing, new { date = "2026-01-02" });

        await SetUnits(series, 4);
        var body = await SetUnits(series, 3);

        body.GetProperty("listEntry").GetProperty("completedUnits").GetInt32().Should().Be(3);
        (await Preference(volumes[2]))!.Status.Should().Be(DeckStatus.Ongoing);
        (await Entries(volumes[2])).Single().StartedOn.Should().Be(new DateOnly(2026, 1, 2));
        (await Preference(volumes[3]))!.Status.Should().Be(DeckStatus.Completed);
        (await Entries(volumes[3])).Single().State.Should().Be(MediaListEntryState.Completed);
    }

    [Fact]
    public async Task FinishBeforeStart_ThroughStatus_EndsOnTheStartDate()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Ongoing, new { date = "2024-06-01" });

        await SetStatus(deckId, DeckStatus.Completed, new { date = "2024-01-01" });

        var entry = (await Entries(deckId)).Single();
        entry.State.Should().Be(MediaListEntryState.Completed);
        entry.FinishedOn.Should().Be(new DateOnly(2024, 6, 1));
    }

    [Fact]
    public async Task Dropped_KeepsTheCompletionUnlessTheUserSaysItNeverHappened()
    {
        var kept = await SeedDeck("Kept");
        var undone = await SeedDeck("Undone");
        await SetStatus(kept, DeckStatus.Completed, new { date = "2024-05-12" });
        await SetStatus(undone, DeckStatus.Completed, new { date = "2024-05-12" });

        await SetStatus(kept, DeckStatus.Dropped);
        await SetStatus(undone, DeckStatus.Dropped, new { undoCompletion = true, date = "2024-06-01" });

        (await Entries(kept)).Single().State.Should().Be(MediaListEntryState.Completed);
        (await Preference(kept))!.Status.Should().Be(DeckStatus.Dropped);
        var dropped = (await Entries(undone)).Single();
        dropped.State.Should().Be(MediaListEntryState.Dropped);
        dropped.FinishedOn.Should().Be(new DateOnly(2024, 6, 1));
    }

    [Fact]
    public async Task DroppingAnUnstartedTitle_RecordsNoPass()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Planning);

        await SetStatus(deckId, DeckStatus.Dropped);

        (await Entries(deckId)).Should().BeEmpty();
        (await Preference(deckId))!.Status.Should().Be(DeckStatus.Dropped);
    }

    [Fact]
    public async Task CompletingAPlannedTitle_LeavesItsSetAsidePassInTheHistory()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Ongoing, new { date = "2024-01-01" });
        await LogCharacters(deckId, 300);
        await SetStatus(deckId, DeckStatus.Planning);

        await SetStatus(deckId, DeckStatus.Completed, new { date = "2024-09-01" });

        (await Entries(deckId)).Select(r => r.State).Should().Equal(MediaListEntryState.Dropped, MediaListEntryState.Completed);
        (await Entries(deckId))[0].CharactersRead.Should().Be(300);
    }

    [Fact]
    public async Task Units_UpAndDown_TakesBackBareCompletionsButKeepsTheSeriesAndAVolumeStartedBefore()
    {
        var (series, volumes) = await SeedSeries(3);
        await SetStatus(series, DeckStatus.Ongoing);
        await SetStatus(volumes[0], DeckStatus.Ongoing);

        await SetUnits(series, 2);
        var body = await SetUnits(series, 0);

        (await Preference(volumes[0]))!.Status.Should().Be(DeckStatus.Ongoing);
        var entry = (await Entries(volumes[0])).Single();
        entry.State.Should().Be(MediaListEntryState.InProgress);
        entry.StartedOn.Should().Be(Today);
        (await Preference(volumes[1])).Should().BeNull();
        (await Entries(volumes[1])).Should().BeEmpty();
        body.GetProperty("status").GetInt32().Should().Be((int)DeckStatus.Ongoing);
        (await Entries(series)).Single().State.Should().Be(MediaListEntryState.InProgress);
    }

    [Fact]
    public async Task PastSeriesRead_KeepsTheVolumesOfTheReadInProgress()
    {
        var (series, volumes) = await SeedSeries(3);
        await SetStatus(volumes[0], DeckStatus.Completed);
        await SetStatus(volumes[1], DeckStatus.Completed);

        (await Send(HttpMethod.Post, $"/api/user/media-list/entries/{series}", new { state = MediaListEntryState.Completed, finishedOn = "2020-01-01" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await (await Send(HttpMethod.Get, $"/api/user/media-list/entries/{series}")).Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("summary").GetProperty("completedUnits").GetInt32().Should().Be(2);
        (await SetUnits(series, 3)).GetProperty("allChildrenCompleted").GetBoolean().Should().BeTrue();
        (await Entries(volumes[0])).Should().ContainSingle();
    }

    [Fact]
    public async Task DeletingAVolumesPastCompletion_OpensAnUntouchedSeries()
    {
        var (series, volumes) = await SeedSeries(2);
        await SetStatus(volumes[0], DeckStatus.Completed);
        await SetStatus(volumes[0], DeckStatus.Ongoing, new { newEntry = true });
        await SetStatus(series, DeckStatus.None);
        var completion = (await Entries(volumes[0])).First();

        var body = await (await Send(HttpMethod.Delete, $"/api/user/media-list/entries/{volumes[0]}/{completion.Id}")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("parentStatus").GetInt32().Should().Be((int)DeckStatus.Ongoing);
    }

    [Fact]
    public async Task PlannedSeries_ShowsItsVolumeProgress()
    {
        var (series, volumes) = await SeedSeries(3);
        await SetStatus(volumes[0], DeckStatus.Completed);
        await SetStatus(series, DeckStatus.Planning);

        var body = await (await Send(HttpMethod.Get, $"/api/user/media-list/entries/{series}")).Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("summary").GetProperty("completedUnits").GetInt32().Should().Be(1);
        body.GetProperty("summary").GetProperty("entryCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task ReadingAgain_KeepsTheTitleRateable()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Completed);
        await SetStatus(deckId, DeckStatus.Ongoing, new { newEntry = true });

        var response = await Send(HttpMethod.Post, "/api/difficulty-votes/rating", new { deckId, rating = 2 });

        response.IsSuccessStatusCode.Should().BeTrue();
    }

    [Theory]
    [InlineData(DeckStatus.Planning)]
    [InlineData(DeckStatus.Dropped)]
    public async Task ReturningToCompleted_KeepsTheCompletionInsteadOfAddingOne(DeckStatus detour)
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Completed, new { date = "2024-05-12" });

        await SetStatus(deckId, detour);
        await SetStatus(deckId, DeckStatus.Completed);

        var entries = await Entries(deckId);
        entries.Should().ContainSingle();
        entries[0].FinishedOn.Should().Be(new DateOnly(2024, 5, 12));
        (await Preference(deckId))!.CurrentEntryId.Should().Be(entries[0].Id);
    }

    [Fact]
    public async Task Import_ADroppedTitleKeepsTheCompletionItPointsAt()
    {
        var deckId = await SeedDeck("VN");

        await Send(HttpMethod.Post, "/api/user/media-list/import/apply", new
        {
            overwriteExisting = true,
            entries = new[]
            {
                new
                {
                    deckId, status = DeckStatus.Dropped,
                    history = new[] { new { state = MediaListEntryState.Completed, finishedOn = "2023-03-01", isCurrent = true } }
                }
            }
        });

        var entry = (await Entries(deckId)).Single();
        entry.State.Should().Be(MediaListEntryState.Completed);
        (await Preference(deckId))!.CurrentEntryId.Should().Be(entry.Id);
    }

    [Fact]
    public async Task Import_ADatedDropOpensADroppedPass()
    {
        var deckId = await SeedDeck("VN");

        await Send(HttpMethod.Post, "/api/user/media-list/import/apply",
                   new { overwriteExisting = true, entries = new[] { new { deckId, status = DeckStatus.Dropped, startedOn = "2023-01-05" } } });

        var entry = (await Entries(deckId)).Single();
        entry.State.Should().Be(MediaListEntryState.Dropped);
        entry.StartedOn.Should().Be(new DateOnly(2023, 1, 5));
    }

    [Fact]
    public async Task Import_AppliesVolumeStatusesAndHistoryFromAJitenExport()
    {
        var (series, volumes) = await SeedSeries(3);

        var response = await Send(HttpMethod.Post, "/api/user/media-list/import/apply", new
        {
            overwriteExisting = true,
            entries = new[]
            {
                new
                {
                    deckId = series, status = DeckStatus.Ongoing, progress = 3,
                    volumes = new object[]
                    {
                        new
                        {
                            deckId = volumes[0], status = DeckStatus.Completed,
                            history = new[]
                            {
                                new { state = MediaListEntryState.Completed, finishedOn = "2023-03-01", isCurrent = false },
                                new { state = MediaListEntryState.Completed, finishedOn = "2024-03-01", isCurrent = true },
                            }
                        },
                        new { deckId = volumes[1], status = DeckStatus.Ongoing, history = (object[]?)null },
                    }
                }
            }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Entries(volumes[0])).Select(r => r.FinishedOn).Should().Equal(new DateOnly(2023, 3, 1), new DateOnly(2024, 3, 1));
        (await Preference(volumes[0]))!.CurrentEntryId.Should().Be((await Entries(volumes[0]))[1].Id);
        (await Preference(volumes[1]))!.Status.Should().Be(DeckStatus.Ongoing);
        (await Preference(volumes[2])).Should().BeNull();
    }

    private async Task<(int Series, List<int> Volumes)> SeriesBeingReadAgain(int volumeCount)
    {
        var (series, volumes) = await SeedSeries(volumeCount);
        await SetUnits(series, volumeCount);
        await SetStatus(series, DeckStatus.Completed);
        await SetStatus(series, DeckStatus.Ongoing, new { newEntry = true });
        return (series, volumes);
    }

    [Fact]
    public async Task SeriesReadAgain_CountsOnlyVolumesFinishedOnThisRead()
    {
        var (series, volumes) = await SeriesBeingReadAgain(3);

        var summary = (await (await Send(HttpMethod.Get, $"/api/user/media-list/entries/{series}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("summary");
        summary.GetProperty("completedUnits").GetInt32().Should().Be(0);

        var body = await SetUnits(series, 1);

        body.GetProperty("listEntry").GetProperty("completedUnits").GetInt32().Should().Be(1);
        (await Entries(volumes[0])).Select(r => r.State).Should().Equal(MediaListEntryState.Completed, MediaListEntryState.Completed);
        (await Entries(volumes[1])).Should().ContainSingle();
    }

    [Fact]
    public async Task SeriesReadAgain_LoweringTakesBackTheReadsCompletion()
    {
        var (series, volumes) = await SeriesBeingReadAgain(2);
        await SetUnits(series, 1);

        var body = await SetUnits(series, 0);

        body.GetProperty("listEntry").GetProperty("completedUnits").GetInt32().Should().Be(0);
        var entries = await Entries(volumes[0]);
        entries.Should().ContainSingle();
        var preference = (await Preference(volumes[0]))!;
        preference.Status.Should().Be(DeckStatus.Completed);
        preference.CurrentEntryId.Should().Be(entries[0].Id);
    }

    [Fact]
    public async Task SeriesReadAgain_OffersToCompleteItOnceEveryVolumeIsReadAgain_AndCountsEachVolumeOncePerPass()
    {
        var (series, _) = await SeriesBeingReadAgain(2);

        (await SetUnits(series, 1)).GetProperty("allChildrenCompleted").GetBoolean().Should().BeFalse();
        (await SetUnits(series, 2)).GetProperty("allChildrenCompleted").GetBoolean().Should().BeTrue();

        await SetStatus(series, DeckStatus.Completed);
        var summary = await Summary(series);
        summary.GetProperty("completedCount").GetInt32().Should().Be(2);
        summary.GetProperty("volumeCharacters").GetInt32().Should().Be(2000);
    }

    private Task<HttpResponseMessage> Import(object entry) =>
        Send(HttpMethod.Post, "/api/user/media-list/import/apply", new { overwriteExisting = true, entries = new[] { entry } });

    private async Task<JsonElement> Summary(int deckId, string userId = TestUsers.UserA)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/media-list/entries/{deckId}").WithUser(userId);
        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("summary");
    }

    [Fact]
    public async Task UpdatingAnEntry_ChangesOnlyTheFieldsSent()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Completed, new { date = "2024-05-12" });
        var url = $"/api/user/media-list/entries/{deckId}/{(await Entries(deckId)).Single().Id}";

        (await Send(HttpMethod.Put, url, new { charactersRead = 300 })).StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = (await Entries(deckId)).Single();
        entry.FinishedOn.Should().Be(new DateOnly(2024, 5, 12));
        entry.CharactersRead.Should().Be(300);

        (await Send(HttpMethod.Put, url, new { finishedOn = (string?)null })).StatusCode.Should().Be(HttpStatusCode.OK);
        entry = (await Entries(deckId)).Single();
        entry.FinishedOn.Should().BeNull();
        entry.CharactersRead.Should().Be(300);
    }

    private async Task FillHistory(int deckId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var existing = await userDb.UserMediaListEntries.CountAsync(r => r.UserId == TestUsers.UserA && r.DeckId == deckId);
        userDb.UserMediaListEntries.AddRange(Enumerable.Range(0, 100 - existing)
                                                       .Select(_ => new UserMediaListEntry
                                                                    {
                                                                        UserId = TestUsers.UserA, DeckId = deckId, State = MediaListEntryState.Completed
                                                                    }));
        await userDb.SaveChangesAsync();
    }

    [Fact]
    public async Task FullHistory_RefusesAChangeThatStartsAnEntry_AndBulkSkipsIt()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Completed);
        await FillHistory(deckId);

        var single = await Send(HttpMethod.Post, $"/api/user/deck-preferences/{deckId}/status", new { status = DeckStatus.Ongoing, newEntry = true });
        var bulk = await Send(HttpMethod.Post, "/api/user/deck-preferences/bulk", new { deckIds = new[] { deckId }, status = DeckStatus.Ongoing });

        single.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await single.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString().Should().Contain("full");
        (await bulk.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("skipped").GetInt32().Should().Be(1);
        (await Entries(deckId)).Should().HaveCount(100);
        (await Preference(deckId))!.Status.Should().Be(DeckStatus.Completed);
    }

    [Fact]
    public async Task PastCompletionOnAVolumeInProgress_ReportsTheSeriesDone()
    {
        var (_, volumes) = await SeedSeries(2);
        await SetStatus(volumes[0], DeckStatus.Completed);
        await SetStatus(volumes[1], DeckStatus.Ongoing);

        var body = await (await Send(HttpMethod.Post, $"/api/user/media-list/entries/{volumes[1]}", new { state = MediaListEntryState.Completed }))
            .Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("status").GetInt32().Should().Be((int)DeckStatus.Ongoing);
        body.GetProperty("allChildrenCompleted").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(DeckStatus.Dropped)]
    [InlineData(DeckStatus.Planning)]
    public async Task Import_AFinishedTitleDroppedOrPlanned_DatesItsLastCompletionInsteadOfAPass(DeckStatus status)
    {
        var deckId = await SeedDeck("VN");

        await Import(new { deckId, status, repeatCount = 2, finishedOn = "2023-03-01", charactersRead = 700 });

        var entries = await Entries(deckId);
        entries.Should().HaveCount(2).And.OnlyContain(r => r.State == MediaListEntryState.Completed);
        entries[1].FinishedOn.Should().Be(new DateOnly(2023, 3, 1));
        entries[1].CharactersRead.Should().Be(700);
        (await Preference(deckId))!.CurrentEntryId.Should().Be(entries[1].Id);
    }

    [Fact]
    public async Task Import_RepeatsOnATitleReadAgain_DateTheLastCompletion()
    {
        var deckId = await SeedDeck("VN");

        await Import(new { deckId, status = DeckStatus.Ongoing, repeatCount = 1, startedOn = "2024-01-01", finishedOn = "2023-03-01" });

        var entries = await Entries(deckId);
        entries.Single(r => r.State == MediaListEntryState.InProgress).StartedOn.Should().Be(new DateOnly(2024, 1, 1));
        entries.Single(r => r.State == MediaListEntryState.Completed).FinishedOn.Should().Be(new DateOnly(2023, 3, 1));
    }

    [Fact]
    public async Task Import_SkipsTheHistoryOfAnIgnoredTitle()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Planning);
        (await Send(HttpMethod.Post, $"/api/user/deck-preferences/{deckId}/ignore", new { isIgnored = true })).StatusCode.Should().Be(HttpStatusCode.OK);

        await Import(new { deckId, status = DeckStatus.Planning, repeatCount = 2 });

        (await Entries(deckId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Import_KeepsASinglePassInProgress()
    {
        var deckId = await SeedDeck("VN");

        await Import(new
        {
            deckId, status = DeckStatus.Ongoing,
            history = new object[]
            {
                new { state = MediaListEntryState.InProgress, startedOn = "2023-01-01" },
                new { state = MediaListEntryState.Completed, finishedOn = "2023-06-01" },
                new { state = MediaListEntryState.InProgress, startedOn = "2024-01-01" },
            }
        });

        var entries = await Entries(deckId);
        entries.Select(r => r.State).Should().Equal(MediaListEntryState.Completed, MediaListEntryState.InProgress);
        entries[1].StartedOn.Should().Be(new DateOnly(2024, 1, 1));
    }

    [Fact]
    public async Task ExportThenImport_KeepsTheVolumesOfASeriesReadAgain()
    {
        var (series, _) = await SeriesBeingReadAgain(3);
        await SetUnits(series, 2);

        var json = await (await Send(HttpMethod.Get, "/api/user/media-list/export?format=json")).Content.ReadAsStringAsync();
        var parsed = JitenExportParser.Parse(json).Entries;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/apply")
                      .WithUser(TestUsers.UserB)
                      .WithJsonContent(new
                                       {
                                           overwriteExisting = true,
                                           entries = parsed.Select(e => new
                                                                        {
                                                                            deckId = e.DeckId, status = e.MappedStatus, history = e.History,
                                                                            volumes = e.Volumes, overwriteSubdecks = true
                                                                        })
                                       });
        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Summary(series)).GetProperty("completedUnits").GetInt32().Should().Be(2);
        (await Summary(series, TestUsers.UserB)).GetProperty("completedUnits").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task VolumeEntries_BelongToTheSeriesPassInProgress()
    {
        var (series, volumes) = await SeedSeries(4);

        await SetStatus(volumes[0], DeckStatus.Completed);
        var firstPass = (await Preference(series))!.CurrentEntryId;
        await SetUnits(series, 2);
        await SetStatus(volumes[2], DeckStatus.Ongoing);
        (await Send(HttpMethod.Post, $"/api/user/media-list/entries/{volumes[3]}", new { state = MediaListEntryState.Completed }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        firstPass.Should().NotBeNull();
        foreach (var volume in volumes.Take(3))
            (await Entries(volume)).Single().SeriesEntryId.Should().Be(firstPass);
        (await Entries(volumes[3])).Single().SeriesEntryId.Should().BeNull();

        await SetStatus(series, DeckStatus.Completed);
        await SetStatus(series, DeckStatus.Ongoing, new { newEntry = true });
        var reread = (await Preference(series))!.CurrentEntryId;
        await SetUnits(series, 1);

        (await Entries(volumes[0])).Select(r => r.SeriesEntryId).Should().Equal(firstPass, reread);
    }

    private async Task<JsonElement> Restore(JsonElement previous, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await Send(HttpMethod.Post, "/api/user/media-list/restore", new { decks = previous });
        response.StatusCode.Should().Be(expected);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Restore_UndoesAStatusChange()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Ongoing, new { date = "2024-02-01" });
        await LogCharacters(deckId, 300);

        var change = await (await SetStatus(deckId, DeckStatus.Completed)).Content.ReadFromJsonAsync<JsonElement>();
        var body = await Restore(change.GetProperty("previous"));

        body.GetProperty("decks")[0].GetProperty("status").GetInt32().Should().Be((int)DeckStatus.Ongoing);
        var entry = (await Entries(deckId)).Single();
        entry.State.Should().Be(MediaListEntryState.InProgress);
        entry.CharactersRead.Should().Be(300);
        entry.FinishedOn.Should().BeNull();
        (await Preference(deckId))!.CurrentEntryId.Should().Be(entry.Id);
    }

    [Fact]
    public async Task Restore_UndoesAUnitsChange()
    {
        var (series, volumes) = await SeedSeries(3);
        await SetUnits(series, 1);

        var change = await SetUnits(series, 3);
        await Restore(change.GetProperty("previous"));

        (await Preference(series))!.Status.Should().Be(DeckStatus.Ongoing);
        (await Entries(volumes[0])).Should().ContainSingle();
        foreach (var volume in volumes.Skip(1))
        {
            (await Preference(volume)).Should().BeNull();
            (await Entries(volume)).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Restore_BringsBackADeletedEntry()
    {
        var deckId = await SeedDeck("VN");
        await SetStatus(deckId, DeckStatus.Completed, new { date = "2024-05-12" });
        await SetStatus(deckId, DeckStatus.Ongoing, new { newEntry = true, date = "2024-12-20" });
        await SetStatus(deckId, DeckStatus.Completed, new { date = "2025-01-03" });
        var deleted = (await Entries(deckId))[1];

        var change = await (await Send(HttpMethod.Delete, $"/api/user/media-list/entries/{deckId}/{deleted.Id}")).Content.ReadFromJsonAsync<JsonElement>();
        await Restore(change.GetProperty("previous"));

        var entries = await Entries(deckId);
        entries.Select(r => r.FinishedOn).Should().Equal(new DateOnly(2024, 5, 12), new DateOnly(2025, 1, 3));
        (await Preference(deckId))!.CurrentEntryId.Should().Be(entries[1].Id);
    }

    [Fact]
    public async Task Restore_RefusesWhatTheOtherEndpointsWould()
    {
        var deckId = await SeedDeck("VN");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/user/deck-preferences/{deckId}/status")
                      .WithUser(TestUsers.UserB)
                      .WithJsonContent(new { status = DeckStatus.Completed });
        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
        long otherUsersEntry;
        using (var scope = factory.Services.CreateScope())
            otherUsersEntry = await scope.ServiceProvider.GetRequiredService<UserDbContext>().UserMediaListEntries
                                         .Where(r => r.UserId == TestUsers.UserB).Select(r => r.Id).SingleAsync();

        object Snapshot(params object[] entries) => new[] { new { deckId, status = DeckStatus.Completed, entries } };
        var completed = new { state = MediaListEntryState.Completed, isCurrent = true };

        foreach (var decks in new[]
                 {
                     Snapshot(new { id = otherUsersEntry, completed.state, completed.isCurrent }),
                     Snapshot(new { completed.state, completed.isCurrent, finishedOn = Today.AddDays(5).ToString("yyyy-MM-dd") }),
                     Snapshot(Enumerable.Range(0, 101).Select(_ => (object)new { state = MediaListEntryState.Completed }).ToArray()),
                 })
            (await Send(HttpMethod.Post, "/api/user/media-list/restore", new { decks })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await Entries(deckId)).Should().BeEmpty();
        using var check = factory.Services.CreateScope();
        (await check.ServiceProvider.GetRequiredService<UserDbContext>().UserMediaListEntries.SingleAsync(r => r.Id == otherUsersEntry))
            .UserId.Should().Be(TestUsers.UserB);
    }
}
