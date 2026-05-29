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
using StackExchange.Redis;

namespace Jiten.Parser.Tests.Integration;

public class CustomDeckTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.UserStudyDeckWords.ExecuteDeleteAsync();
        await userDb.UserStudyDecks.ExecuteDeleteAsync();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateStaticWordListDeck()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 2, name = "My Word List", description = "Test list", downloadType = 1, order = 4, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<IdResult>();
        result!.UserStudyDeckId.Should().BeGreaterThan(0);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var deck = await userDb.UserStudyDecks.FirstAsync(sd => sd.UserStudyDeckId == result.UserStudyDeckId);
        deck.DeckType.Should().Be(StudyDeckType.StaticWordList);
        deck.Name.Should().Be("My Word List");
        deck.Description.Should().Be("Test list");
        deck.DeckId.Should().BeNull();
    }

    [Fact]
    public async Task CreateGlobalDynamicDeck()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 1, name = "Top 5000", minGlobalFrequency = 1, maxGlobalFrequency = 5000, downloadType = 1, order = 2, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<IdResult>();
        result!.UserStudyDeckId.Should().BeGreaterThan(0);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var deck = await userDb.UserStudyDecks.FirstAsync(sd => sd.UserStudyDeckId == result.UserStudyDeckId);
        deck.DeckType.Should().Be(StudyDeckType.GlobalDynamic);
        deck.Name.Should().Be("Top 5000");
        deck.MinGlobalFrequency.Should().Be(1);
        deck.MaxGlobalFrequency.Should().Be(5000);
    }

    [Fact]
    public async Task GlobalDynamic_RequiresName()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 1, minGlobalFrequency = 1, maxGlobalFrequency = 5000, downloadType = 1, order = 2, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GlobalDynamic_RequiresFrequencyBound()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 1, name = "Test", downloadType = 1, order = 2, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StaticDeck_AddAndRemoveWord()
    {
        await SeedJmDictWords(100, 100);

        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 2, name = "Words", downloadType = 1, order = 4, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        var createRes = await _client.SendAsync(createReq);
        var deckResult = await createRes.Content.ReadFromJsonAsync<IdResult>();
        var deckId = deckResult!.UserStudyDeckId;

        var addReq = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/words")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { wordId = 100, readingIndex = 0, occurrences = 3 });
        var addRes = await _client.SendAsync(addReq);
        addRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var word = await userDb.UserStudyDeckWords.FirstAsync(w => w.UserStudyDeckId == deckId && w.WordId == 100);
            word.Occurrences.Should().Be(3);
            word.ReadingIndex.Should().Be(0);
        }

        var dupReq = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/words")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { wordId = 100, readingIndex = 0, occurrences = 2 });
        var dupRes = await _client.SendAsync(dupReq);
        dupRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var word = await userDb.UserStudyDeckWords.FirstAsync(w => w.UserStudyDeckId == deckId && w.WordId == 100);
            word.Occurrences.Should().Be(5);
        }

        var removeReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/srs/study-decks/{deckId}/words/100/0")
            .WithUser(TestUsers.UserA);
        var removeRes = await _client.SendAsync(removeReq);
        removeRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var exists = await userDb.UserStudyDeckWords.AnyAsync(w => w.UserStudyDeckId == deckId && w.WordId == 100);
            exists.Should().BeFalse();
        }
    }

    [Fact]
    public async Task StaticDeck_BatchAddWords()
    {
        await SeedJmDictWords(1, 50);

        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 2, name = "Batch Test", downloadType = 1, order = 4, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        var createRes = await _client.SendAsync(createReq);
        var deckResult = await createRes.Content.ReadFromJsonAsync<IdResult>();
        var deckId = deckResult!.UserStudyDeckId;

        var words = Enumerable.Range(1, 50).Select(i => new { wordId = i, readingIndex = 0, occurrences = 1 }).ToArray();
        var batchReq = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/words/batch")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { words });
        var batchRes = await _client.SendAsync(batchReq);
        batchRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await batchRes.Content.ReadFromJsonAsync<AddedResult>();
        result!.Added.Should().Be(50);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var count = await userDb.UserStudyDeckWords.CountAsync(w => w.UserStudyDeckId == deckId);
        count.Should().Be(50);
    }

    [Fact]
    public async Task StaticDeck_UpdateWordOccurrences()
    {
        await SeedJmDictWords(200, 200);

        // Seed an additional word form with readingIndex=1
        using (var seedScope = factory.Services.CreateScope())
        {
            var jitenDb = seedScope.ServiceProvider.GetRequiredService<JitenDbContext>();
            if (!await jitenDb.WordForms.AnyAsync(wf => wf.WordId == 200 && wf.ReadingIndex == 1))
                jitenDb.WordForms.Add(new JmDictWordForm { WordId = 200, ReadingIndex = 1, Text = "word200alt", RubyText = "word200alt", FormType = JmDictFormType.KanaForm });
            await jitenDb.SaveChangesAsync();
        }

        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 2, name = "Update Test", downloadType = 1, order = 4, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        var createRes = await _client.SendAsync(createReq);
        var deckResult = await createRes.Content.ReadFromJsonAsync<IdResult>();
        var deckId = deckResult!.UserStudyDeckId;

        var addReq = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/words")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { wordId = 200, readingIndex = 1, occurrences = 1 });
        await _client.SendAsync(addReq);

        var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/srs/study-decks/{deckId}/words/200/1")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { occurrences = 7 });
        var patchRes = await _client.SendAsync(patchReq);
        patchRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var word = await userDb.UserStudyDeckWords.FirstAsync(w => w.UserStudyDeckId == deckId && w.WordId == 200);
        word.Occurrences.Should().Be(7);
    }

    [Fact]
    public async Task CannotAddWordsToMediaDeck()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        if (!await jitenDb.Decks.AnyAsync(d => d.DeckId == 99))
        {
            jitenDb.Decks.Add(new Jiten.Core.Data.Deck { DeckId = 99, OriginalTitle = "Media Deck", MediaType = Jiten.Core.Data.MediaType.Anime, CreationDate = DateTime.UtcNow, CharacterCount = 1, WordCount = 1, UniqueWordCount = 1 });
            await jitenDb.SaveChangesAsync();
        }

        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 0, deckId = 99, downloadType = 1, order = 2, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        var createRes = await _client.SendAsync(createReq);
        var deckResult = await createRes.Content.ReadFromJsonAsync<IdResult>();
        var deckId = deckResult!.UserStudyDeckId;

        var addReq = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/words")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { wordId = 1, readingIndex = 0 });
        var addRes = await _client.SendAsync(addReq);
        addRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CascadeDelete_RemovesDeckWords()
    {
        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 2, name = "Cascade Test", downloadType = 1, order = 4, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        var createRes = await _client.SendAsync(createReq);
        var deckResult = await createRes.Content.ReadFromJsonAsync<IdResult>();
        var deckId = deckResult!.UserStudyDeckId;

        var words = Enumerable.Range(1, 10).Select(i => new { wordId = i, readingIndex = 0 }).ToArray();
        var batchReq = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/words/batch")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { words });
        await _client.SendAsync(batchReq);

        var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/srs/study-decks/{deckId}")
            .WithUser(TestUsers.UserA);
        var deleteRes = await _client.SendAsync(deleteReq);
        deleteRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var remainingWords = await userDb.UserStudyDeckWords.CountAsync(w => w.UserStudyDeckId == deckId);
        remainingWords.Should().Be(0);
    }

    [Fact]
    public async Task DeckLimit_50Decks()
    {
        for (var i = 0; i < 50; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(new { deckType = 2, name = $"Deck {i}", downloadType = 1, order = 4, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
            var res = await _client.SendAsync(req);
            res.StatusCode.Should().Be(HttpStatusCode.OK, $"Deck {i} should be created successfully");
        }

        var overflowReq = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 2, name = "Overflow", downloadType = 1, order = 4, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        var overflowRes = await _client.SendAsync(overflowReq);
        overflowRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetStudyDecks_ReturnsAllDeckTypes()
    {
        var mediaReq = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 2, name = "Word List", downloadType = 1, order = 4, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        await _client.SendAsync(mediaReq);

        var globalReq = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 1, name = "Global Top", minGlobalFrequency = 1, maxGlobalFrequency = 100, downloadType = 1, order = 2, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        await _client.SendAsync(globalReq);

        var getReq = new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA);
        var getRes = await _client.SendAsync(getReq);
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var decks = await getRes.Content.ReadFromJsonAsync<List<DeckDto>>();
        decks.Should().HaveCount(2);
        decks!.Should().Contain(d => d.DeckType == 2 && d.Name == "Word List");
        decks!.Should().Contain(d => d.DeckType == 1 && d.Name == "Global Top");
    }

    [Fact]
    public async Task StaticDeckWords_AppearInStudyBatch()
    {
        await SeedJmDictWords(1, 5);

        var createRes = await CreateStaticDeck("Batch Words");
        var deckId = (await createRes.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;

        var words = Enumerable.Range(1, 3).Select(i => new { wordId = i, readingIndex = 0, occurrences = 1 }).ToArray();
        var batchAdd = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/words/batch")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { words });
        (await _client.SendAsync(batchAdd)).StatusCode.Should().Be(HttpStatusCode.OK);

        var batch = new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-batch?limit=10")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(batch);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var cards = body.GetProperty("cards");
        cards.GetArrayLength().Should().BeGreaterThan(0);

        var cardWordIds = Enumerable.Range(0, cards.GetArrayLength())
            .Select(i => cards[i].GetProperty("wordId").GetInt32())
            .ToList();
        cardWordIds.Should().Contain(i => i >= 1 && i <= 3);
    }



    [Fact]
    public async Task BatchAdd_RejectsWhenExceeding200KTotalLimit()
    {
        // Fill 5 decks with 40,000 words each = 200,000 total
        var deckIds = new List<int>();
        for (var d = 0; d < 5; d++)
        {
            var res = await CreateStaticDeck($"Deck {d}");
            deckIds.Add((await res.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId);

            using var scope = factory.Services.CreateScope();
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            for (var i = 0; i < 40_000; i++)
            {
                userDb.UserStudyDeckWords.Add(new UserStudyDeckWord
                {
                    UserStudyDeckId = deckIds[d],
                    WordId = d * 40_000 + i + 1,
                    ReadingIndex = 0,
                    SortOrder = i
                });
            }
            await userDb.SaveChangesAsync();
        }

        // Total is 200,000. Adding 1 word should exceed 200K
        var words = new[] { new { wordId = 900_001, readingIndex = 0, occurrences = 1 } };
        var batchReq = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckIds[4]}/words/batch")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { words });
        var batchRes = await _client.SendAsync(batchReq);
        batchRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await batchRes.Content.ReadAsStringAsync();
        body.Should().Contain("200,000");
    }

    [Fact]
    public async Task GlobalDynamic_WordsAppearInStudyBatch()
    {
        await SeedWordFormFrequencies(1, 10);

        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                deckType = 1,
                name = "Global Freq",
                minGlobalFrequency = 1,
                maxGlobalFrequency = 5,
                downloadType = 1,
                order = 2,
                minFrequency = 0,
                maxFrequency = 0,
                excludeKana = false,
                excludeMatureMasteredBlacklisted = false,
                excludeAllTrackedWords = false
            });
        var createRes = await _client.SendAsync(createReq);
        createRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var batch = new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-batch?limit=10")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(batch);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var cards = body.GetProperty("cards");
        cards.GetArrayLength().Should().BeGreaterThan(0);

        var cardWordIds = Enumerable.Range(0, cards.GetArrayLength())
            .Select(i => cards[i].GetProperty("wordId").GetInt32())
            .ToList();
        cardWordIds.Should().AllSatisfy(id => id.Should().BeInRange(1, 5));
    }

    [Fact]
    public async Task StudyDecksOnly_FiltersReviewsToStudyDeckWords()
    {
        await SeedJmDictWords(1, 10);

        var createStatic = await CreateStaticDeck("My Static Deck");
        var staticDeckId = (await createStatic.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;

        var staticWords = new[] { new { wordId = 1, readingIndex = 0, occurrences = 1 }, new { wordId = 2, readingIndex = 0, occurrences = 1 } };
        var addWords = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{staticDeckId}/words/batch")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { words = staticWords });
        (await _client.SendAsync(addWords)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Create FSRS cards for words 1, 2, and 5 — word 5 is NOT in the static deck
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            foreach (var wordId in new[] { 1, 2, 5 })
            {
                userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, 0,
                    state: FsrsState.Review,
                    stability: 1.0,
                    difficulty: 5.0,
                    due: DateTime.UtcNow.AddDays(-1),
                    lastReview: DateTime.UtcNow.AddDays(-2)));
            }
            await userDb.SaveChangesAsync();
        }

        // Set ReviewFrom = StudyDecksOnly
        var updateSettings = new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                newCardsPerDay = 0,
                maxReviewsPerDay = 200,
                gradingButtons = 4,
                interleaving = "mixed",

                reviewFrom = "studyDecksOnly"
            });
        (await _client.SendAsync(updateSettings)).StatusCode.Should().Be(HttpStatusCode.OK);

        var batch = new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-batch?limit=20")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(batch);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        var cards = body.GetProperty("cards");
        var cardWordIds = Enumerable.Range(0, cards.GetArrayLength())
            .Select(i => cards[i].GetProperty("wordId").GetInt32())
            .ToList();

        cardWordIds.Should().NotContain(5, "word 5 is not in any study deck and should be filtered out");
        cardWordIds.Should().BeSubsetOf(new[] { 1, 2 });
    }

    [Fact]
    public async Task ImportCommit_CreatesStaticDeckFromPreviewToken()
    {
        // Seed preview data directly into the in-memory Redis mock
        var previewToken = Guid.NewGuid().ToString("N");
        var previewData = JsonSerializer.Serialize(new[]
        {
            new { WordId = 10, ReadingIndex = (short)0, Text = "食べる", Reading = "たべる", Occurrences = 1 },
            new { WordId = 20, ReadingIndex = (short)0, Text = "飲む", Reading = "のむ", Occurrences = 1 },
            new { WordId = 30, ReadingIndex = (short)0, Text = "走る", Reading = "はしる", Occurrences = 1 }
        });

        using (var scope = factory.Services.CreateScope())
        {
            var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
            var db = redis.GetDatabase();
            await db.StringSetAsync($"import-preview:{previewToken}", previewData, TimeSpan.FromMinutes(5));
        }

        var commitReq = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks/import")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { previewToken, name = "Imported Words" });
        var commitRes = await _client.SendAsync(commitReq);
        commitRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var commit = JsonSerializer.Deserialize<JsonElement>(await commitRes.Content.ReadAsStringAsync());
        var importedDeckId = commit.GetProperty("userStudyDeckId").GetInt32();
        importedDeckId.Should().BeGreaterThan(0);

        using var verifyScope = factory.Services.CreateScope();
        var userDb = verifyScope.ServiceProvider.GetRequiredService<UserDbContext>();
        var deck = await userDb.UserStudyDecks.FirstAsync(sd => sd.UserStudyDeckId == importedDeckId);
        deck.DeckType.Should().Be(StudyDeckType.StaticWordList);
        deck.Name.Should().Be("Imported Words");

        var wordCount = await userDb.UserStudyDeckWords.CountAsync(w => w.UserStudyDeckId == importedDeckId);
        wordCount.Should().Be(3);
    }

    private async Task<HttpResponseMessage> CreateStaticDeck(string name)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 2, name, downloadType = 1, order = 4, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return res;
    }

    private async Task SeedJmDictWords(int from, int to)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        for (var i = from; i <= to; i++)
        {
            if (!await jitenDb.JMDictWords.AnyAsync(w => w.WordId == i))
                jitenDb.JMDictWords.Add(new JmDictWord { WordId = i, PartsOfSpeech = ["noun"] });
        }
        await jitenDb.SaveChangesAsync();

        for (var i = from; i <= to; i++)
        {
            if (!await jitenDb.Definitions.AnyAsync(d => d.WordId == i))
                jitenDb.Definitions.Add(new JmDictDefinition { WordId = i, SenseIndex = 0, EnglishMeanings = [$"meaning{i}"], PartsOfSpeech = ["noun"] });
        }
        await jitenDb.SaveChangesAsync();

        for (var i = from; i <= to; i++)
        {
            if (!await jitenDb.WordForms.AnyAsync(wf => wf.WordId == i))
                jitenDb.WordForms.Add(new JmDictWordForm { WordId = i, ReadingIndex = 0, Text = $"word{i}", RubyText = $"word{i}", FormType = JmDictFormType.KanaForm });
        }
        await jitenDb.SaveChangesAsync();
    }

    private async Task SeedWordFormFrequencies(int from, int to)
    {
        await SeedJmDictWords(from, to);

        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        for (var i = from; i <= to; i++)
        {
            if (!await jitenDb.WordFormFrequencies.AnyAsync(wff => wff.WordId == i && wff.ReadingIndex == 0))
            {
                jitenDb.WordFormFrequencies.Add(new JmDictWordFormFrequency
                {
                    WordId = i,
                    ReadingIndex = 0,
                    FrequencyRank = i,
                    FrequencyPercentage = 0.01,
                    ObservedFrequency = 100.0 / i,
                    UsedInMediaAmount = 10
                });
            }
        }
        await jitenDb.SaveChangesAsync();
    }

    [Fact]
    public async Task PreviewCount_GlobalDynamic_ReturnsCorrectCount()
    {
        await SeedWordFormFrequencies(1, 20);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks/preview-count")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                deckType = 1,
                name = "Preview",
                minGlobalFrequency = 1,
                maxGlobalFrequency = 10,
                downloadType = 1,
                order = 2,
                minFrequency = 0,
                maxFrequency = 0,
                excludeKana = false,
                excludeMatureMasteredBlacklisted = false,
                excludeAllTrackedWords = false
            });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<PreviewCountResult>();
        result!.Total.Should().Be(10);
    }

    [Fact]
    public async Task PreviewCount_GlobalDynamic_ExcludeKana()
    {
        await SeedWordFormFrequencies(1, 5);

        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var existingForm = await jitenDb.WordForms.FirstAsync(wf => wf.WordId == 1);
            existingForm.FormType = JmDictFormType.KanjiForm;
            await jitenDb.SaveChangesAsync();
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks/preview-count")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                deckType = 1,
                name = "Preview",
                minGlobalFrequency = 1,
                maxGlobalFrequency = 5,
                downloadType = 1,
                order = 2,
                minFrequency = 0,
                maxFrequency = 0,
                excludeKana = true,
                excludeMatureMasteredBlacklisted = false,
                excludeAllTrackedWords = false
            });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<PreviewCountResult>();
        result!.Total.Should().Be(1, "only word 1 has a non-kana form");
    }

    [Fact]
    public async Task ReorderStudyDecks_UpdatesSortOrder()
    {
        var res1 = await CreateStaticDeck("Deck A");
        var id1 = (await res1.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;
        var res2 = await CreateStaticDeck("Deck B");
        var id2 = (await res2.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;
        var res3 = await CreateStaticDeck("Deck C");
        var id3 = (await res3.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;

        var reorderReq = new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-decks/reorder")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                items = new[]
                {
                    new { userStudyDeckId = id3, sortOrder = 0 },
                    new { userStudyDeckId = id1, sortOrder = 1 },
                    new { userStudyDeckId = id2, sortOrder = 2 }
                }
            });
        var reorderRes = await _client.SendAsync(reorderReq);
        reorderRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var decks = await userDb.UserStudyDecks
            .Where(sd => sd.UserId == TestUsers.UserA)
            .OrderBy(sd => sd.SortOrder)
            .ToListAsync();

        decks[0].UserStudyDeckId.Should().Be(id3);
        decks[1].UserStudyDeckId.Should().Be(id1);
        decks[2].UserStudyDeckId.Should().Be(id2);
    }

    [Fact]
    public async Task ReorderStudyDecks_RejectsDuplicateIds()
    {
        var res1 = await CreateStaticDeck("Deck X");
        var id1 = (await res1.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;

        var req = new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-decks/reorder")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                items = new[]
                {
                    new { userStudyDeckId = id1, sortOrder = 0 },
                    new { userStudyDeckId = id1, sortOrder = 1 }
                }
            });
        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateStudyDeck_NameAndDescription()
    {
        var createRes = await CreateStaticDeck("Original Name");
        var deckId = (await createRes.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;

        var updateReq = new HttpRequestMessage(HttpMethod.Put, $"/api/srs/study-decks/{deckId}")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                name = "Updated Name",
                description = "A new description",
                downloadType = 1,
                order = 4,
                excludeKana = false,
                excludeMatureMasteredBlacklisted = true,
                excludeAllTrackedWords = false
            });
        var updateRes = await _client.SendAsync(updateReq);
        updateRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var deck = await userDb.UserStudyDecks.FirstAsync(sd => sd.UserStudyDeckId == deckId);
        deck.Name.Should().Be("Updated Name");
        deck.Description.Should().Be("A new description");
    }

    [Fact]
    public async Task UpdateStudyDeck_GlobalDynamic_UpdatesFilters()
    {
        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                deckType = 1,
                name = "Global Freq",
                minGlobalFrequency = 1,
                maxGlobalFrequency = 100,
                downloadType = 1,
                order = 2,
                minFrequency = 0,
                maxFrequency = 0,
                excludeKana = false,
                excludeMatureMasteredBlacklisted = false,
                excludeAllTrackedWords = false
            });
        var createRes = await _client.SendAsync(createReq);
        var deckId = (await createRes.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;

        var updateReq = new HttpRequestMessage(HttpMethod.Put, $"/api/srs/study-decks/{deckId}")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                name = "Updated Global",
                minGlobalFrequency = 50,
                maxGlobalFrequency = 500,
                downloadType = 1,
                order = 2,
                excludeKana = true,
                excludeMatureMasteredBlacklisted = true,
                excludeAllTrackedWords = false
            });
        var updateRes = await _client.SendAsync(updateReq);
        updateRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var deck = await userDb.UserStudyDecks.FirstAsync(sd => sd.UserStudyDeckId == deckId);
        deck.Name.Should().Be("Updated Global");
        deck.MinGlobalFrequency.Should().Be(50);
        deck.MaxGlobalFrequency.Should().Be(500);
        deck.ExcludeKana.Should().BeTrue();
    }

    [Fact]
    public async Task GetDeckWords_ReturnsPaginatedWords()
    {
        await SeedJmDictWords(1, 5);

        var createRes = await CreateStaticDeck("Word Listing");
        var deckId = (await createRes.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;

        var words = Enumerable.Range(1, 5).Select(i => new { wordId = i, readingIndex = 0, occurrences = i }).ToArray();
        var batchReq = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/words/batch")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { words });
        (await _client.SendAsync(batchReq)).StatusCode.Should().Be(HttpStatusCode.OK);

        var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/srs/study-decks/{deckId}/words")
            .WithUser(TestUsers.UserA);
        var getRes = await _client.SendAsync(getReq);
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await getRes.Content.ReadAsStringAsync());
        var data = body.GetProperty("data");
        data.GetProperty("deckName").GetString().Should().Be("Word Listing");
        data.GetProperty("words").GetArrayLength().Should().Be(5);
        body.GetProperty("totalItems").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task GetDeckWords_SearchByJapaneseText()
    {
        await SeedJmDictWords(1, 3);

        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var forms = await jitenDb.WordForms.Where(wf => wf.WordId >= 1 && wf.WordId <= 3).ToListAsync();
            forms.First(f => f.WordId == 1).Text = "食べる";
            forms.First(f => f.WordId == 1).RubyText = "たべる";
            forms.First(f => f.WordId == 2).Text = "飲む";
            forms.First(f => f.WordId == 2).RubyText = "のむ";
            forms.First(f => f.WordId == 3).Text = "走る";
            forms.First(f => f.WordId == 3).RubyText = "はしる";
            await jitenDb.SaveChangesAsync();
        }

        var createRes = await CreateStaticDeck("Searchable");
        var deckId = (await createRes.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;

        var words = new[] { new { wordId = 1, readingIndex = 0 }, new { wordId = 2, readingIndex = 0 }, new { wordId = 3, readingIndex = 0 } };
        var batchReq = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/words/batch")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { words });
        (await _client.SendAsync(batchReq)).StatusCode.Should().Be(HttpStatusCode.OK);

        var searchReq = new HttpRequestMessage(HttpMethod.Get, $"/api/srs/study-decks/{deckId}/words?search=食べ")
            .WithUser(TestUsers.UserA);
        var searchRes = await _client.SendAsync(searchReq);
        searchRes.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await searchRes.Content.ReadAsStringAsync());
        body.GetProperty("data").GetProperty("words").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task GetDeckWords_RejectsNonStaticDeck()
    {
        var createReq = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 1, name = "Global", minGlobalFrequency = 1, maxGlobalFrequency = 100, downloadType = 1, order = 2, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = false, excludeAllTrackedWords = false });
        var createRes = await _client.SendAsync(createReq);
        var deckId = (await createRes.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;

        var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/srs/study-decks/{deckId}/words")
            .WithUser(TestUsers.UserA);
        var getRes = await _client.SendAsync(getReq);
        getRes.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CrossUser_CannotAccessOtherUsersDeck()
    {
        var createRes = await CreateStaticDeck("UserA Private");
        var deckId = (await createRes.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;

        // UserB tries to update UserA's deck
        var updateReq = new HttpRequestMessage(HttpMethod.Put, $"/api/srs/study-decks/{deckId}")
            .WithUser(TestUsers.UserB)
            .WithJsonContent(new { name = "Hacked", downloadType = 1, order = 4, excludeKana = false, excludeMatureMasteredBlacklisted = false, excludeAllTrackedWords = false });
        var updateRes = await _client.SendAsync(updateReq);
        updateRes.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // UserB tries to delete UserA's deck
        var deleteReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/srs/study-decks/{deckId}")
            .WithUser(TestUsers.UserB);
        var deleteRes = await _client.SendAsync(deleteReq);
        deleteRes.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // UserB tries to add words to UserA's deck
        var addReq = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/words")
            .WithUser(TestUsers.UserB)
            .WithJsonContent(new { wordId = 1, readingIndex = 0 });
        var addRes = await _client.SendAsync(addReq);
        addRes.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // UserB tries to list words in UserA's deck
        var listReq = new HttpRequestMessage(HttpMethod.Get, $"/api/srs/study-decks/{deckId}/words")
            .WithUser(TestUsers.UserB);
        var listRes = await _client.SendAsync(listReq);
        listRes.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // UserB's deck list should not contain UserA's deck
        var getDecksReq = new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-decks")
            .WithUser(TestUsers.UserB);
        var getDecksRes = await _client.SendAsync(getDecksReq);
        var decks = await getDecksRes.Content.ReadFromJsonAsync<List<DeckDto>>();
        decks.Should().NotContain(d => d.UserStudyDeckId == deckId);
    }

    [Fact]
    public async Task CrossUser_ReorderIgnoresOtherUsersDecks()
    {
        var createRes = await CreateStaticDeck("UserA Deck");
        var deckId = (await createRes.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;

        // UserB tries to reorder UserA's deck — should succeed but not affect UserA's deck
        var reorderReq = new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-decks/reorder")
            .WithUser(TestUsers.UserB)
            .WithJsonContent(new { items = new[] { new { userStudyDeckId = deckId, sortOrder = 99 } } });
        var reorderRes = await _client.SendAsync(reorderReq);
        reorderRes.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var deck = await userDb.UserStudyDecks.FirstAsync(sd => sd.UserStudyDeckId == deckId);
        deck.SortOrder.Should().NotBe(99, "UserB should not be able to reorder UserA's deck");
    }

    private record IdResult(int UserStudyDeckId);
    private record AddedResult(int Added);
    private record PreviewCountResult(int Total, int Unlearned);
    private record DeckDto(int UserStudyDeckId, int DeckType, string Name, int? DeckId, int TotalWords);
}
