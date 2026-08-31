using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class WordExampleSentencesTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int WordId = 4242;
    private int[] _studyDeckIds = [];
    private int[] _otherDeckIds = [];
    private long[] _studySentenceIds = [];

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        await jitenDb.DeckWords.Where(dw => dw.WordId == WordId).ExecuteDeleteAsync();
        await jitenDb.ExampleSentenceWords.ExecuteDeleteAsync();
        await jitenDb.ExampleSentences.ExecuteDeleteAsync();
        await jitenDb.JMDictWords.Where(w => w.WordId == WordId).ExecuteDeleteAsync();
        await userDb.UserStudyDecks.Where(d => d.UserId == TestUsers.UserA).ExecuteDeleteAsync();
        await userDb.UserExampleSentences.Where(e => e.UserId == TestUsers.UserA).ExecuteDeleteAsync();

        jitenDb.JMDictWords.Add(new JmDictWord { WordId = WordId, PartsOfSpeech = ["noun"] });

        var studyA = NewDeck("Study Deck A");
        var studyB = NewDeck("Study Deck B");
        var otherA = NewDeck("Other A");
        var otherB = NewDeck("Other B");
        var otherC = NewDeck("Other C");
        jitenDb.Decks.AddRange(studyA, studyB, otherA, otherB, otherC);
        await jitenDb.SaveChangesAsync();

        _studyDeckIds = [studyA.DeckId, studyB.DeckId];
        _otherDeckIds = [otherA.DeckId, otherB.DeckId, otherC.DeckId];

        _studySentenceIds =
        [
            await AddSentence(jitenDb, studyA.DeckId, "study deck A sentence", 0.2f),
            await AddSentence(jitenDb, studyB.DeckId, "study deck B sentence", 0.2f),
        ];
        foreach (var (deckId, i) in _otherDeckIds.Select((d, i) => (d, i)))
            await AddSentence(jitenDb, deckId, $"general sentence {i}", 0.2f);

        // Inserted directly: the tracked Deck entities would otherwise be re-inserted by this save and
        // the DeckWord foreign keys rewritten to the new ids.
        foreach (var deckId in _otherDeckIds.Concat(_studyDeckIds))
        {
            await jitenDb.Database.ExecuteSqlRawAsync(
                "INSERT INTO DeckWords (DeckId, WordId, ReadingIndex, Occurrences) VALUES ({0}, {1}, 0, 1)",
                deckId, WordId);
        }

        foreach (var (deckId, i) in _studyDeckIds.Select((d, i) => (d, i)))
        {
            userDb.UserStudyDecks.Add(new UserStudyDeck
            {
                UserId = TestUsers.UserA,
                DeckType = StudyDeckType.MediaDeck,
                Name = $"Study Deck {i}",
                DeckId = deckId,
            });
        }
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Deck NewDeck(string title) => new()
    {
        OriginalTitle = title,
        MediaType = MediaType.Novel,
        ReleaseDate = new DateOnly(2020, 1, 1),
    };

    private static async Task<long> AddSentence(JitenDbContext db, int deckId, string text, float difficulty)
    {
        var sentence = new ExampleSentence { DeckId = deckId, Text = text, Difficulty = difficulty };
        db.ExampleSentences.Add(sentence);
        await db.SaveChangesAsync();

        db.ExampleSentenceWords.Add(new ExampleSentenceWord
        {
            ExampleSentenceId = sentence.SentenceId, WordId = WordId, ReadingIndex = 0, Position = 0, Length = 2,
        });
        await db.SaveChangesAsync();
        return sentence.SentenceId;
    }

    private async Task<SentencesResponse> Query(object payload, string? userId = TestUsers.UserA)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/word-example-sentences")
            .WithJsonContent(payload);
        if (userId != null) request.WithUser(userId);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SentencesResponse>())!;
    }

    [Fact]
    public async Task Random_PutsStudyDeckSentenceFirst()
    {
        var result = await Query(new { wordId = WordId, readingIndex = 0, sorting = "Random", take = 3 });

        result.Sentences.Should().HaveCount(3);
        result.Sentences.Take(2).Should().OnlyContain(s => s.FromStudyDeck);
        result.Sentences.Take(2).Select(s => s.SentenceId).Should().BeEquivalentTo(_studySentenceIds);
        result.Sentences[2].FromStudyDeck.Should().BeFalse();
    }

    [Fact]
    public async Task Random_TopsUpFromGeneralPool()
    {
        var result = await Query(new { wordId = WordId, readingIndex = 0, sorting = "Random", take = 3 });

        result.Sentences.Select(s => s.SourceDeck!.DeckId).Should().OnlyHaveUniqueItems();
        result.Sentences.Count(s => s.FromStudyDeck).Should().Be(2);
    }

    [Fact]
    public async Task Random_HonoursExcludedDeckIds()
    {
        var result = await Query(new
        {
            wordId = WordId, readingIndex = 0, sorting = "Random", take = 3,
            excludedDeckIds = _studyDeckIds.Append(_otherDeckIds[0]).ToArray(),
        });

        result.Sentences.Should().HaveCount(2);
        result.Sentences.Should().NotContain(s => s.FromStudyDeck);
        result.Sentences.Select(s => s.SourceDeck!.DeckId).Should().NotIntersectWith(_studyDeckIds);
    }

    [Fact]
    public async Task Random_WithoutStudyDecks_ReturnsGeneralSentencesOnly()
    {
        var result = await Query(new { wordId = WordId, readingIndex = 0, sorting = "Random", take = 4 },
                                 TestUsers.UserB);

        result.Sentences.Should().HaveCount(4);
        result.Sentences.Should().NotContain(s => s.FromStudyDeck);
    }

    [Fact]
    public async Task Difficulty_PrefersStudyDeckWithinTheBand()
    {
        var result = await Query(new
        {
            wordId = WordId, readingIndex = 0, sorting = "EasiestFirst",
            minDifficulty = 0f, maxDifficulty = 0.5f, descending = false, take = 3,
        });

        result.Sentences.Should().NotBeEmpty();
        result.Sentences[0].FromStudyDeck.Should().BeTrue();
        _studySentenceIds.Should().Contain(result.Sentences[0].SentenceId);
    }

    private async Task SetExampleSentenceSource(string source)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var settings = await userDb.UserFsrsSettings.FirstOrDefaultAsync(s => s.UserId == TestUsers.UserA);
        if (settings == null)
        {
            settings = new UserFsrsSettings { UserId = TestUsers.UserA };
            userDb.UserFsrsSettings.Add(settings);
        }
        settings.SettingsJson = $"{{\"exampleSentenceSource\":\"{source}\"}}";
        await userDb.SaveChangesAsync();
    }

    private async Task<long?> CardExampleSentenceId()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/card-examples")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { pairs = new[] { new { wordId = WordId, readingIndex = 0 } } });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<CardExamplesPayload>();
        return payload!.Examples.GetValueOrDefault($"{WordId}-0")?.SentenceId;
    }

    [Fact]
    public async Task CardExamples_RandomSource_VariesAcrossCalls()
    {
        await SetExampleSentenceSource("Random");

        var seen = new HashSet<long>();
        for (var i = 0; i < 20; i++)
        {
            var id = await CardExampleSentenceId();
            id.Should().NotBeNull();
            seen.Add(id!.Value);
        }

        seen.Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task CardExamples_StudyDeckSource_VariesBetweenStudyDecks()
    {
        await SetExampleSentenceSource("StudyDecks");

        var seen = new HashSet<long>();
        for (var i = 0; i < 20; i++)
        {
            var id = await CardExampleSentenceId();
            id.Should().NotBeNull();
            seen.Add(id!.Value);
        }

        seen.Should().HaveCount(2);
        seen.Should().BeSubsetOf(_studySentenceIds);
    }

    [Fact]
    public async Task CardExamples_RandomSource_StillPrefersCustomSentences()
    {
        await SetExampleSentenceSource("Random");

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.UserExampleSentences.Where(e => e.UserId == TestUsers.UserA && e.WordId == WordId).ExecuteDeleteAsync();
            userDb.UserExampleSentences.Add(new UserExampleSentence
            {
                UserId = TestUsers.UserA, WordId = WordId, ReadingIndex = 0,
                Text = "これは**猫**です", SortOrder = 0, CreatedAt = DateTime.UtcNow,
            });
            await userDb.SaveChangesAsync();
        }

        for (var i = 0; i < 5; i++)
            (await CardExampleSentenceId()).Should().BeNegative();
    }

    [Fact]
    public async Task CardExamples_SentenceIdAboveIntMax_RoundTrips()
    {
        const int bigWordId = 5555;
        const long bigSentenceId = 3_000_000_000;
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            await jitenDb.JMDictWords.Where(w => w.WordId == bigWordId).ExecuteDeleteAsync();
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = bigWordId, PartsOfSpeech = ["noun"] });
            jitenDb.ExampleSentences.Add(new ExampleSentence
            {
                SentenceId = bigSentenceId, DeckId = _otherDeckIds[0], Text = "big id sentence", Difficulty = 0.2f,
            });
            await jitenDb.SaveChangesAsync();
            jitenDb.ExampleSentenceWords.Add(new ExampleSentenceWord
            {
                ExampleSentenceId = bigSentenceId, WordId = bigWordId, ReadingIndex = 0, Position = 0, Length = 2,
            });
            await jitenDb.SaveChangesAsync();
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/card-examples")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { pairs = new[] { new { wordId = bigWordId, readingIndex = 0 } } });
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<CardExamplesPayload>();
        payload!.Examples.GetValueOrDefault($"{bigWordId}-0")!.SentenceId.Should().Be(bigSentenceId);
    }

    [Fact]
    public async Task Anonymous_IsRejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/word-example-sentences")
            .WithJsonContent(new { wordId = WordId, readingIndex = 0, sorting = "Random" });
        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private class SentencesResponse
    {
        public List<SentenceDto> Sentences { get; set; } = [];
    }

    private class SentenceDto
    {
        public long SentenceId { get; set; }
        public string Text { get; set; } = "";
        public bool FromStudyDeck { get; set; }
        public DeckRef? SourceDeck { get; set; }
    }

    private class DeckRef
    {
        public int DeckId { get; set; }
    }

    private class CardExamplesPayload
    {
        public Dictionary<string, SentenceDto> Examples { get; set; } = new();
    }
}
