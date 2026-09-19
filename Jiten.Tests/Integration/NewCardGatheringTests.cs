using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class NewCardGatheringTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int MediaDeckId = 1;
    private const int WordCount = 8;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        await Seed();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task Seed()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        await userDb.UserStudyDeckWords.ExecuteDeleteAsync();
        await userDb.UserStudyDecks.ExecuteDeleteAsync();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.FsrsCardArchives.ExecuteDeleteAsync();
        await userDb.UserReviewDailies.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();

        await jitenDb.DeckWords.ExecuteDeleteAsync();
        await jitenDb.Definitions.ExecuteDeleteAsync();
        await jitenDb.WordForms.ExecuteDeleteAsync();
        await jitenDb.JMDictWords.ExecuteDeleteAsync();
        await jitenDb.Decks.ExecuteDeleteAsync();

        var deck = new Deck
        {
            DeckId = MediaDeckId,
            OriginalTitle = "Media",
            MediaType = MediaType.Anime,
            CreationDate = DateTime.UtcNow,
            CharacterCount = 1000,
            WordCount = 500,
            UniqueWordCount = 100,
        };
        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();

        for (var i = 1; i <= WordCount; i++)
        {
            jitenDb.DeckWords.Add(new DeckWord { Deck = deck, WordId = i, ReadingIndex = 0, Occurrences = 100 - i });
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = i, PartsOfSpeech = ["noun"] });
        }
        await jitenDb.SaveChangesAsync();

        for (var i = 1; i <= WordCount; i++)
        {
            jitenDb.Definitions.Add(new JmDictDefinition { WordId = i, SenseIndex = 0, EnglishMeanings = [$"meaning{i}"], PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = i, ReadingIndex = 0, Text = $"word{i}", RubyText = $"word{i}", FormType = JmDictFormType.KanaForm });
        }
        await jitenDb.SaveChangesAsync();
    }

    private async Task SetGathering(string gathering, int newCardsPerDay)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                newCardsPerDay,
                maxReviewsPerDay = 200,
                gradingButtons = 4,
                interleaving = "newFirst",
                reviewFrom = "allTracked",
                newCardGathering = gathering
            }));
        response.EnsureSuccessStatusCode();
    }

    private async Task<int> AddWordListDeck(string name, params int[] wordIds)
    {
        var add = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 2, name, order = 4 }));
        add.EnsureSuccessStatusCode();
        var id = (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userStudyDeckId").GetInt32();

        foreach (var wordId in wordIds)
        {
            var word = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{id}/words")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(new { wordId, readingIndex = 0 }));
            word.EnsureSuccessStatusCode();
        }
        return id;
    }

    private async Task<int> AddMediaDeck()
    {
        var add = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckId = MediaDeckId, downloadType = 1, order = 3, excludeKana = false }));
        add.EnsureSuccessStatusCode();
        return (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userStudyDeckId").GetInt32();
    }

    private async Task<List<int>> FetchNewCards(int? extraNewCards = null)
    {
        var url = "/api/srs/study-batch?limit=20" + (extraNewCards.HasValue ? $"&extraNewCards={extraNewCards}" : "");
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).WithUser(TestUsers.UserA));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("cards").EnumerateArray()
            .Where(c => c.GetProperty("isNewCard").GetBoolean())
            .Select(c => c.GetProperty("wordId").GetInt32())
            .ToList();
    }

    private async Task Review(IEnumerable<int> wordIds)
    {
        foreach (var wordId in wordIds)
        {
            var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(new { wordId, readingIndex = 0, rating = 3, clientRequestId = Guid.NewGuid().ToString("N") }));
            response.EnsureSuccessStatusCode();
        }
    }

    private async Task SetCursor(int? studyDeckId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var row = await userDb.UserFsrsSettings.FirstOrDefaultAsync(s => s.UserId == TestUsers.UserA);
        if (row == null)
        {
            row = new UserFsrsSettings { UserId = TestUsers.UserA };
            userDb.UserFsrsSettings.Add(row);
        }
        row.NewCardCursorStudyDeckId = studyDeckId;
        await userDb.SaveChangesAsync();
    }

    private async Task<int?> GetCursor()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserFsrsSettings.Where(s => s.UserId == TestUsers.UserA)
            .Select(s => s.NewCardCursorStudyDeckId).FirstOrDefaultAsync();
    }

    private async Task SetActive(int studyDeckId, bool isActive, params int[] allDeckIds)
    {
        var items = allDeckIds.Select((id, i) => new { userStudyDeckId = id, sortOrder = i, isActive = id != studyDeckId || isActive });
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-decks/reorder")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { items }));
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task RoundRobin_ContinuesWhereThePreviousBatchStopped()
    {
        await SetGathering("roundRobin", 2);
        var a = await AddWordListDeck("A", 1, 2);
        var b = await AddWordListDeck("B", 3);
        var c = await AddWordListDeck("C", 4, 5);

        var first = await FetchNewCards();
        first.Should().Equal(1, 3);
        (await GetCursor()).Should().BeNull("fetching alone does not move the cursor");
        await Review(first);
        (await GetCursor()).Should().Be(c);

        var second = await FetchNewCards(extraNewCards: 2);
        second.Should().Equal(4, 2);
        await Review(second);
        (await GetCursor()).Should().Be(c, "B has nothing left, so it holds no lane and the cursor skips it");

        (await FetchNewCards(extraNewCards: 2)).Should().Equal([5]);
        _ = (a, b);
    }

    [Fact]
    public async Task RoundRobin_UnreviewedBatchKeepsTheCursorInPlace()
    {
        await SetGathering("roundRobin", 2);
        await AddWordListDeck("A", 1, 2);
        await AddWordListDeck("B", 3);
        await AddWordListDeck("C", 4, 5);

        (await FetchNewCards()).Should().Equal(1, 3);
        (await FetchNewCards()).Should().Equal([1, 3], "nothing was graded, so the next batch starts from the same deck");
    }

    [Fact]
    public async Task RoundRobin_PartiallyReviewedBatchResumesAfterTheLastGradedCard()
    {
        await SetGathering("roundRobin", 3);
        var a = await AddWordListDeck("A", 1, 2);
        var b = await AddWordListDeck("B", 3, 4);
        var c = await AddWordListDeck("C", 5, 6);

        (await FetchNewCards()).Should().Equal(1, 3, 5);
        await Review([1, 3]);
        (await GetCursor()).Should().Be(c, "the last graded card came from B, so C is next");

        (await FetchNewCards(extraNewCards: 3)).Should().Equal([5, 2, 4]);
        _ = (a, b);
    }

    [Fact]
    public async Task RoundRobin_GradingOutsideAStudyBatchLeavesTheCursorAlone()
    {
        await SetGathering("roundRobin", 2);
        await AddWordListDeck("A", 1, 2);
        await AddWordListDeck("B", 3);

        await Review([2]);
        (await GetCursor()).Should().BeNull();
    }

    [Fact]
    public async Task RoundRobin_CursorOnDeletedDeckRestartsAtTop()
    {
        await SetGathering("roundRobin", 2);
        await AddWordListDeck("A", 1, 2);
        await AddWordListDeck("B", 3);
        await SetCursor(9999);

        (await FetchNewCards()).Should().Equal(1, 3);
    }

    [Fact]
    public async Task RoundRobin_CursorOnDeactivatedDeckHandsOverToTheNextOne()
    {
        await SetGathering("roundRobin", 2);
        var a = await AddWordListDeck("A", 1, 2);
        var b = await AddWordListDeck("B", 3);
        var c = await AddWordListDeck("C", 4, 5);
        await SetCursor(b);
        await SetActive(b, false, a, b, c);

        (await FetchNewCards()).Should().Equal(4, 1);
    }

    [Fact]
    public async Task RoundRobin_EveryDeckGetsATurnWhenBudgetIsBelowDeckCount()
    {
        await SetGathering("roundRobin", 3);
        for (var i = 1; i <= 5; i++)
            await AddWordListDeck($"D{i}", i);

        var day1 = await FetchNewCards();
        await Review(day1);
        var day2 = await FetchNewCards(extraNewCards: 3);

        day1.Should().Equal(1, 2, 3);
        day2.Should().Equal([4, 5], "the reviewed words are gone and the trailing decks finally get their turn");
    }

    [Fact]
    public async Task RoundRobin_LaterDecksKeepTheirOwnOrderWhenWordsOverlap()
    {
        await SetGathering("roundRobin", 4);
        await AddWordListDeck("A", 1, 2, 3, 4);
        await AddWordListDeck("B", 2, 1, 5, 6);
        await AddWordListDeck("C", 3, 2, 1, 7);

        (await FetchNewCards()).Should().Equal([1, 2, 3, 4], "each deck yields its first unclaimed word in its own order");
    }

    [Fact]
    public async Task TopDeck_DrainsTheFirstDeckBeforeTheSecond()
    {
        await SetGathering("topDeck", 2);
        await AddWordListDeck("A", 1, 2);
        await AddWordListDeck("B", 3);

        (await FetchNewCards()).Should().Equal(1, 2);
        (await GetCursor()).Should().BeNull();
    }

    [Fact]
    public async Task CrossDeckFrequency_NonMediaDecksTakeTurnsWithTheRankedMediaLane()
    {
        await SetGathering("crossDeckFrequency", 4);
        var list = await AddWordListDeck("List", 8);
        await AddMediaDeck();

        var cards = await FetchNewCards();
        cards.Should().Equal([1, 8, 2, 3], "the media lane keeps occurrence order and the list deck gets every other slot");
        await Review(cards);
        (await GetCursor()).Should().Be(list);
    }

    [Fact]
    public async Task CrossDeckFrequency_OnlyMediaDecksIsUnchanged()
    {
        await SetGathering("crossDeckFrequency", 4);
        await AddMediaDeck();

        (await FetchNewCards()).Should().Equal(1, 2, 3, 4);
        (await GetCursor()).Should().BeNull();
    }
}
