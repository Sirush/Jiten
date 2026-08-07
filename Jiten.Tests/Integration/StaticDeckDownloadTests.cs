using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// Download / learn / count parity for static word list study decks.
/// Word list: word1..word5, import order 0..4, occurrences 50/30/10/5/5 (total 100),
/// global frequency ranks 100/50/10/2000/30000.
/// </summary>
public class StaticDeckDownloadTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();
    private int _deckId;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        await SeedStaticDeck();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedStaticDeck()
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

        await jitenDb.WordFormFrequencies.ExecuteDeleteAsync();
        await jitenDb.Definitions.ExecuteDeleteAsync();
        await jitenDb.WordForms.ExecuteDeleteAsync();
        await jitenDb.JMDictWords.ExecuteDeleteAsync();

        int[] ranks = [100, 50, 10, 2000, 30000];
        for (var i = 1; i <= 5; i++)
        {
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = i, PartsOfSpeech = ["noun"] });
            jitenDb.Definitions.Add(new JmDictDefinition
            {
                WordId = i, SenseIndex = 0, EnglishMeanings = [$"meaning{i}"], PartsOfSpeech = ["noun"],
            });
            jitenDb.WordForms.Add(new JmDictWordForm
            {
                WordId = i, ReadingIndex = 0, Text = $"word{i}", RubyText = $"word{i}",
                FormType = JmDictFormType.KanjiForm,
            });
            jitenDb.WordFormFrequencies.Add(new JmDictWordFormFrequency
            {
                WordId = i, ReadingIndex = 0, FrequencyRank = ranks[i - 1],
            });
        }
        await jitenDb.SaveChangesAsync();

        var deck = new UserStudyDeck
        {
            UserId = TestUsers.UserA,
            DeckType = StudyDeckType.StaticWordList,
            Name = "My List",
        };
        userDb.UserStudyDecks.Add(deck);
        await userDb.SaveChangesAsync();
        _deckId = deck.UserStudyDeckId;

        int[] occurrences = [50, 30, 10, 5, 5];
        for (var i = 1; i <= 5; i++)
        {
            userDb.UserStudyDeckWords.Add(new UserStudyDeckWord
            {
                UserStudyDeckId = deck.UserStudyDeckId, WordId = i, ReadingIndex = 0,
                SortOrder = i - 1, Occurrences = occurrences[i - 1],
            });
        }
        await userDb.SaveChangesAsync();
    }

    private async Task<string[]> DownloadTxtLines(object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{_deckId}/download")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(payload);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
    }

    [Fact]
    public async Task Download_Full_ReturnsAllWordsInImportOrder()
    {
        // Txt = 3, Full = 1, ImportOrder = 4
        var lines = await DownloadTxtLines(new { format = 3, downloadType = 1, order = 4 });
        lines.Should().Equal("word1", "word2", "word3", "word4", "word5");
    }

    [Fact]
    public async Task Download_TopChronological_SlicesRange()
    {
        // TopChronological = 4: first two words in list order
        var lines = await DownloadTxtLines(new { format = 3, downloadType = 4, order = 4, minFrequency = 0, maxFrequency = 2 });
        lines.Should().Equal("word1", "word2");
    }

    [Fact]
    public async Task Download_TopDeckFrequency_SlicesByOccurrences()
    {
        // TopDeckFrequency = 3: top 3 by occurrences (50/30/10)
        var lines = await DownloadTxtLines(new { format = 3, downloadType = 3, order = 3, minFrequency = 0, maxFrequency = 3 });
        lines.Should().Equal("word1", "word2", "word3");
    }

    [Fact]
    public async Task Download_TopGlobalFrequency_FiltersByRank()
    {
        // TopGlobalFrequency = 2: ranks within [1, 200] are word3(10), word2(50), word1(100); GlobalFrequency order = 2
        var lines = await DownloadTxtLines(new { format = 3, downloadType = 2, order = 2, minFrequency = 1, maxFrequency = 200 });
        lines.Should().Equal("word3", "word2", "word1");
    }

    [Fact]
    public async Task Download_OccurrenceCount_FiltersByThreshold()
    {
        // OccurrenceCount = 6, DeckFrequency order = 3
        var lines = await DownloadTxtLines(new { format = 3, downloadType = 6, order = 3, minOccurrences = 10 });
        lines.Should().Equal("word1", "word2", "word3");
    }

    [Fact]
    public async Task Download_TargetCoverage_CollectsCheapestPath()
    {
        // TargetCoverage = 5: 80% of 100 total occurrences = word1(50) + word2(30)
        var lines = await DownloadTxtLines(new { format = 3, downloadType = 5, order = 3, targetPercentage = 80 });
        lines.Should().Equal("word1", "word2");
    }

    [Fact]
    public async Task Download_TargetCoverage_WithoutPercentage_ReturnsBadRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{_deckId}/download")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { format = 3, downloadType = 5, order = 3 });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Download_Yomitan_ReturnsZip()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{_deckId}/download")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { format = 5, downloadType = 1, order = 4 });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        (await response.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Learn_Mastered_CreatesMasteredCards()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{_deckId}/learn")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { vocabularyState = "mastered", downloadType = 1, order = 4 });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("applied").GetInt32().Should().Be(5);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var cards = await userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA).ToListAsync();
        cards.Should().HaveCount(5);
        cards.Should().OnlyContain(c => c.State == FsrsState.Mastered);
    }

    [Fact]
    public async Task Learn_Blacklisted_RespectsOccurrenceFilter()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{_deckId}/learn")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { vocabularyState = "blacklisted", downloadType = 6, order = 4, minOccurrences = 30 });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("applied").GetInt32().Should().Be(2);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var cards = await userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA).ToListAsync();
        cards.Should().HaveCount(2);
        cards.Select(c => c.WordId).Should().BeEquivalentTo([1, 2]);
        cards.Should().OnlyContain(c => c.State == FsrsState.Blacklisted);
    }

    [Fact]
    public async Task Learn_InvalidState_ReturnsBadRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{_deckId}/learn")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { vocabularyState = "nonsense", downloadType = 1, order = 4 });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Learn_OtherUsersDeck_Returns404()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{_deckId}/learn")
            .WithUser(TestUsers.UserB)
            .WithJsonContent(new { vocabularyState = "mastered", downloadType = 1, order = 4 });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task VocabularyCountOccurrences_ReturnsFilteredCount()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/srs/study-decks/{_deckId}/vocabulary-count-occurrences?minOccurrences=10")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<int>()).Should().Be(3);
    }

    [Fact]
    public async Task VocabularyCountFrequency_ReturnsFilteredCount()
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
                $"/api/srs/study-decks/{_deckId}/vocabulary-count-frequency?minFrequency=1&maxFrequency=200")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<int>()).Should().Be(3);
    }

    [Fact]
    public async Task VocabularyCount_Post_HonoursDownloadFilters()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{_deckId}/vocabulary-count")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { format = 3, downloadType = 6, order = 4, minOccurrences = 30 });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<int>()).Should().Be(2);
    }

    [Fact]
    public async Task VocabularyCount_Post_TargetCoverage()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{_deckId}/vocabulary-count")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { format = 3, downloadType = 5, order = 3, targetPercentage = 80 });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<int>()).Should().Be(2);
    }
}
