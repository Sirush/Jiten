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

public class MediaListDatesImportTests(JitenWebApplicationFactory factory)
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

    private static readonly DateOnly Start = new(2023, 2, 10);
    private static readonly DateOnly Finish = new(2023, 3, 20);

    private async Task<int> SeedDeck(string title, string? anilistId = null)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var deck = new Deck { OriginalTitle = title, MediaType = MediaType.Anime };
        if (anilistId != null)
            deck.Links.Add(new Link { LinkType = LinkType.Anilist, Url = $"https://anilist.co/anime/{anilistId}" });
        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();
        return deck.DeckId;
    }

    /// <param name="passes">Oldest first; the last one is the current pass.</param>
    private async Task SeedTitle(int deckId, DeckStatus status, bool isIgnored = false, params UserMediaListEntry[] passes)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        foreach (var pass in passes)
        {
            pass.UserId = TestUsers.UserA;
            pass.DeckId = deckId;
            userDb.UserMediaListEntries.Add(pass);
        }

        await userDb.SaveChangesAsync();
        userDb.UserDeckPreferences.Add(new UserDeckPreference
                                       {
                                           UserId = TestUsers.UserA, DeckId = deckId, Status = status, IsIgnored = isIgnored,
                                           CurrentEntryId = passes.LastOrDefault()?.Id
                                       });
        await userDb.SaveChangesAsync();
    }

    private async Task<List<UserMediaListEntry>> Passes(int deckId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserMediaListEntries.AsNoTracking().Where(r => r.UserId == TestUsers.UserA && r.DeckId == deckId).OrderBy(r => r.Id)
                           .ToListAsync();
    }

    private async Task<JsonElement> ImportDates(int deckId, DeckStatus status, DateOnly? startedOn, DateOnly? finishedOn)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/dates")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { entries = new[] { new { deckId, status, startedOn, finishedOn } } });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task UndatedCompletion_TakesBothDates()
    {
        var deckId = await SeedDeck("Show");
        await SeedTitle(deckId, DeckStatus.Completed, passes: new UserMediaListEntry { State = MediaListEntryState.Completed });

        var body = await ImportDates(deckId, DeckStatus.Completed, Start, Finish);

        body.GetProperty("dated").GetInt32().Should().Be(1);
        var pass = (await Passes(deckId)).Single();
        pass.StartedOn.Should().Be(Start);
        pass.FinishedOn.Should().Be(Finish);
    }

    [Fact]
    public async Task DateAlreadySet_IsKept_AndOnlyTheEmptyOneIsFilled()
    {
        var deckId = await SeedDeck("Show");
        var ownFinish = new DateOnly(2023, 4, 1);
        await SeedTitle(deckId, DeckStatus.Completed, passes: new UserMediaListEntry { State = MediaListEntryState.Completed, FinishedOn = ownFinish });

        await ImportDates(deckId, DeckStatus.Completed, Start, Finish);

        var pass = (await Passes(deckId)).Single();
        pass.FinishedOn.Should().Be(ownFinish);
        pass.StartedOn.Should().Be(Start);
    }

    [Fact]
    public async Task StatusThatDescribesAnotherPass_IsSkipped()
    {
        var deckId = await SeedDeck("Show");
        await SeedTitle(deckId, DeckStatus.Ongoing, passes: new UserMediaListEntry { State = MediaListEntryState.InProgress });

        var body = await ImportDates(deckId, DeckStatus.Completed, Start, Finish);

        body.GetProperty("dated").GetInt32().Should().Be(0);
        body.GetProperty("skipped").GetInt32().Should().Be(1);
        (await Passes(deckId)).Single().StartedOn.Should().BeNull();
    }

    [Fact]
    public async Task PausedSourceOnAnOngoingTitle_FillsOnlyTheStart()
    {
        var deckId = await SeedDeck("Show");
        await SeedTitle(deckId, DeckStatus.Ongoing, passes: new UserMediaListEntry { State = MediaListEntryState.InProgress });

        await ImportDates(deckId, DeckStatus.Paused, Start, Finish);

        var pass = (await Passes(deckId)).Single();
        pass.StartedOn.Should().Be(Start);
        pass.FinishedOn.Should().BeNull();
    }

    [Fact]
    public async Task StartAfterTheFinish_IsLeftOut()
    {
        var deckId = await SeedDeck("Show");
        await SeedTitle(deckId, DeckStatus.Completed, passes: new UserMediaListEntry { State = MediaListEntryState.Completed, FinishedOn = Start });

        var body = await ImportDates(deckId, DeckStatus.Completed, Finish, null);

        body.GetProperty("dated").GetInt32().Should().Be(0);
        (await Passes(deckId)).Single().StartedOn.Should().BeNull();
    }

    [Fact]
    public async Task OnlyTheCurrentCompletionIsDated_AndNoPassIsAdded()
    {
        var deckId = await SeedDeck("Show");
        await SeedTitle(deckId, DeckStatus.Completed, passes:
                        [
                            new UserMediaListEntry { State = MediaListEntryState.Completed },
                            new UserMediaListEntry { State = MediaListEntryState.Completed },
                        ]);

        await ImportDates(deckId, DeckStatus.Completed, Start, Finish);

        var passes = await Passes(deckId);
        passes.Should().HaveCount(2);
        passes[0].FinishedOn.Should().BeNull();
        passes[1].FinishedOn.Should().Be(Finish);
    }

    [Fact]
    public async Task IgnoredTitle_IsSkipped()
    {
        var deckId = await SeedDeck("Show");
        await SeedTitle(deckId, DeckStatus.Completed, isIgnored: true, passes: new UserMediaListEntry { State = MediaListEntryState.Completed });

        await ImportDates(deckId, DeckStatus.Completed, Start, Finish);

        (await Passes(deckId)).Single().FinishedOn.Should().BeNull();
    }

    [Fact]
    public async Task Preview_ReportsThePassToDateAndDatePrecision()
    {
        var dated = await SeedDeck("Dated", "100");
        var differs = await SeedDeck("Differs", "200");
        await SeedTitle(dated, DeckStatus.Completed, passes: new UserMediaListEntry { State = MediaListEntryState.Completed, FinishedOn = Finish });
        await SeedTitle(differs, DeckStatus.Planning);

        factory.ExternalLists.Result = new ExternalListFetchResult(
        [
            new ExternalListEntry("100", "Dated", "https://anilist.co/anime/100", "COMPLETED", DeckStatus.Completed, Finish,
                                  StartedOn: new DateOnly(2023, 2, 1), CompletedOn: Finish, StartedPrecision: DatePrecision.Month),
            new ExternalListEntry("200", "Differs", "https://anilist.co/anime/200", "COMPLETED", DeckStatus.Completed, Finish, CompletedOn: Finish),
        ], null);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/media-list/import/preview")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { provider = "anilist", username = "tester" });
        var body = await (await _client.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();
        var rows = body.GetProperty("matched").EnumerateArray().ToDictionary(r => r.GetProperty("deckId").GetInt32());

        var target = rows[dated].GetProperty("datesTarget");
        target.GetProperty("inProgress").GetBoolean().Should().BeFalse();
        target.GetProperty("startedOn").ValueKind.Should().Be(JsonValueKind.Null);
        target.GetProperty("finishedOn").GetString().Should().Be("2023-03-20");
        rows[dated].GetProperty("startedPrecision").GetString().Should().Be("month");
        rows[dated].GetProperty("completedPrecision").GetString().Should().Be("day");
        rows[differs].GetProperty("datesTarget").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
