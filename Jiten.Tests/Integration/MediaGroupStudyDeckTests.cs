using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class MediaGroupStudyDeckTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int Franchise = 1;
    private const int Series = 2;
    private const int Line = 3;

    private const int SagaId = 1;
    private const int OutsideDeckId = 4;
    private int _franchiseId;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        await Seed();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Saga holds 1 (anime), 2 (novel) and 3 (anime). Deck 4 sits outside. 2 is a sequel of 1, so the line
    // anchored at 1 is {1, 2}, and the franchise is {1, 2, 3}.
    // Merged occurrences: w1 = 10 + 1, w2 = 5 + 7, w3 = 3, w4 = 20; chronological order w1, w2, w3, w4.
    private async Task Seed()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();
        await db.DeckRelationships.ExecuteDeleteAsync();
        await db.DeckWords.ExecuteDeleteAsync();
        await db.Definitions.ExecuteDeleteAsync();
        await db.WordForms.ExecuteDeleteAsync();
        await db.JMDictWords.ExecuteDeleteAsync();

        db.Decks.AddRange(Deck(1, "Anime one", MediaType.Anime, 2000, 15), Deck(2, "Novel one", MediaType.Novel, 2001, 10),
                          Deck(3, "Anime two", MediaType.Anime, 2002, 21), Deck(OutsideDeckId, "Outside", MediaType.Anime, 2003, 50));
        await db.SaveChangesAsync();

        (int Deck, int Word, int Occurrences)[] rows = [(1, 1, 10), (1, 2, 5), (2, 2, 7), (2, 3, 3), (3, 4, 20), (3, 1, 1), (4, 5, 50)];
        foreach (var (deckId, wordId, occurrences) in rows)
        {
            db.DeckWords.Add(new DeckWord { DeckId = deckId, WordId = wordId, ReadingIndex = 0, Occurrences = occurrences });
            await db.SaveChangesAsync();
        }

        for (var wordId = 1; wordId <= 5; wordId++)
        {
            db.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = ["n"] });
            db.Definitions.Add(new JmDictDefinition { WordId = wordId, SenseIndex = 0, EnglishMeanings = [$"meaning{wordId}"], PartsOfSpeech = ["n"] });
            db.WordForms.Add(new JmDictWordForm { WordId = wordId, ReadingIndex = 0, Text = $"語{wordId}", RubyText = $"語{wordId}", FormType = JmDictFormType.KanjiForm });
        }

        db.DeckRelationships.Add(Rel(2, 1));

        var saga = new Series { SeriesId = SagaId, Name = "Saga", Kind = SeriesKind.Series };
        saga.Members.Add(new SeriesMember { SeriesId = SagaId, DeckId = 1 });
        saga.Members.Add(new SeriesMember { SeriesId = SagaId, DeckId = 2 });
        saga.Members.Add(new SeriesMember { SeriesId = SagaId, DeckId = 3 });
        db.Series.Add(saga);
        await db.SaveChangesAsync();

        _franchiseId = await SyncAsync(1);
    }

    private async Task<int> SyncAsync(int deckId)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<FranchiseSyncRunner>().RunAsync();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        return (await db.Decks.AsNoTracking().Where(d => d.DeckId == deckId).Select(d => d.FranchiseId).SingleAsync())!.Value;
    }

    private static Deck Deck(int id, string title, MediaType mediaType, int year, int wordCount = 0) =>
        new()
        {
            DeckId = id, OriginalTitle = title, RomajiTitle = $"{title} (romaji)", MediaType = mediaType, CreationDate = DateTime.UtcNow,
            ReleaseDate = new DateOnly(year, 1, 1), CharacterCount = 1000, WordCount = wordCount, UniqueWordCount = 10
        };

    private static DeckRelationship Rel(int source, int target) =>
        new() { SourceDeckId = source, TargetDeckId = target, RelationshipType = DeckRelationshipType.Sequel };

    private static object GroupBody(int kind = Series, int id = SagaId, int[]? mediaTypes = null, int[]? excluded = null, int downloadType = 1,
                                    int order = 3, int? minOccurrences = null, float? targetPercentage = null,
                                    int minFrequency = 0, int maxFrequency = 0) =>
        new
        {
            deckType = 4, groupKind = kind, groupId = id, groupMediaTypes = mediaTypes, groupExcludedDeckIds = excluded, downloadType, order,
            minOccurrences, targetPercentage, minFrequency, maxFrequency
        };

    private Task<HttpResponseMessage> AddAsync(object body) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks").WithUser(TestUsers.UserA).WithJsonContent(body));

    private async Task<int> AddOkAsync(object body)
    {
        var response = await AddAsync(body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userStudyDeckId").GetInt32();
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url).WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<List<(int WordId, int Occurrences)>> VocabularyAsync(int studyDeckId, string sortBy = "deckFreq")
    {
        var body = await GetJsonAsync($"/api/srs/study-decks/{studyDeckId}/vocabulary?sortBy={sortBy}");
        return body.GetProperty("data").EnumerateArray()
                   .Select(w => (w.GetProperty("wordId").GetInt32(), w.GetProperty("occurrences").GetInt32()))
                   .ToList();
    }

    private async Task<JsonElement> StudyDeckRowAsync(int studyDeckId) =>
        (await GetJsonAsync("/api/srs/study-decks")).EnumerateArray()
                                                     .Single(d => d.GetProperty("userStudyDeckId").GetInt32() == studyDeckId);

    [Fact]
    public async Task Create_ListsTheSeriesWithMergedCounts()
    {
        var id = await AddOkAsync(GroupBody());

        var row = await StudyDeckRowAsync(id);

        row.GetProperty("deckType").GetInt32().Should().Be(4);
        row.GetProperty("groupKind").GetInt32().Should().Be(Series);
        row.GetProperty("groupId").GetInt32().Should().Be(SagaId);
        row.GetProperty("groupName").GetString().Should().Be("Saga");
        row.GetProperty("groupTitles").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("groupFranchiseId").GetInt32().Should().Be(_franchiseId);
        row.GetProperty("title").GetString().Should().Be("Saga");
        row.GetProperty("totalWords").GetInt32().Should().Be(4);
        row.GetProperty("groupMediaTypes").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Franchise_StudiesEveryDeckOfTheFranchise()
    {
        var id = await AddOkAsync(GroupBody(kind: Franchise, id: _franchiseId));

        var row = await StudyDeckRowAsync(id);
        row.GetProperty("groupKind").GetInt32().Should().Be(Franchise);
        row.GetProperty("groupName").GetString().Should().Be("Saga");
        row.GetProperty("groupFranchiseId").GetInt32().Should().Be(_franchiseId);
        row.GetProperty("title").GetString().Should().Be("Saga");
        row.GetProperty("totalWords").GetInt32().Should().Be(4);

        (await VocabularyAsync(id)).Should().Equal((4, 20), (2, 12), (1, 11), (3, 3));
    }

    [Fact]
    public async Task Line_StudiesTheStoryLineAndCarriesTheAnchorTitles()
    {
        var id = await AddOkAsync(GroupBody(kind: Line, id: 1));

        var row = await StudyDeckRowAsync(id);
        row.GetProperty("groupKind").GetInt32().Should().Be(Line);
        row.GetProperty("groupName").GetString().Should().Be("Anime one");
        row.GetProperty("groupTitles").GetProperty("originalTitle").GetString().Should().Be("Anime one");
        row.GetProperty("groupTitles").GetProperty("romajiTitle").GetString().Should().Be("Anime one (romaji)");
        row.GetProperty("groupFranchiseId").GetInt32().Should().Be(_franchiseId);
        row.GetProperty("title").GetString().Should().Be("Anime one");

        (await VocabularyAsync(id)).Should().Equal((2, 12), (1, 10), (3, 3));
    }

    [Fact]
    public async Task Vocabulary_MergesTheSeriesAndSumsOccurrences()
    {
        var id = await AddOkAsync(GroupBody());

        (await VocabularyAsync(id)).Should().Equal((4, 20), (2, 12), (1, 11), (3, 3));
        (await VocabularyAsync(id, "chrono")).Select(w => w.WordId).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task MediaTypesAndExclusions_NarrowTheWordSet()
    {
        var novelsOnly = await AddOkAsync(GroupBody(mediaTypes: [(int)MediaType.Novel]));
        var withoutSideStory = await AddOkAsync(GroupBody(excluded: [3]));
        var franchiseAnime = await AddOkAsync(GroupBody(kind: Franchise, id: _franchiseId, mediaTypes: [(int)MediaType.Anime], excluded: [3]));

        (await VocabularyAsync(novelsOnly)).Should().Equal((2, 7), (3, 3));
        (await VocabularyAsync(withoutSideStory)).Should().Equal((2, 12), (1, 10), (3, 3));
        (await VocabularyAsync(franchiseAnime)).Should().Equal((1, 10), (2, 5));

        var row = await StudyDeckRowAsync(withoutSideStory);
        row.GetProperty("groupExcludedDeckIds").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(3);
    }

    [Fact]
    public async Task Update_ChangesTheFilters()
    {
        var id = await AddOkAsync(GroupBody());

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/srs/study-decks/{id}")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new
                                               {
                                                   downloadType = 1, order = 3, groupMediaTypes = new[] { (int)MediaType.Anime },
                                                   groupExcludedDeckIds = new[] { 1 }
                                               }));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        (await VocabularyAsync(id)).Should().Equal((4, 20), (1, 1));
    }

    [Fact]
    public async Task Update_RejectsAnExclusionOutsideTheGroup()
    {
        var id = await AddOkAsync(GroupBody(kind: Line, id: 1));

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/srs/study-decks/{id}")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { downloadType = 1, order = 3, groupExcludedDeckIds = new[] { 3 } }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OccurrenceFilter_AppliesToMergedOccurrences()
    {
        var id = await AddOkAsync(GroupBody(downloadType: 6, minOccurrences: 12));

        (await VocabularyAsync(id)).Select(w => w.WordId).Should().Equal(4, 2);
        (await StudyDeckRowAsync(id)).GetProperty("totalWords").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task TopChronological_UsesEachWordsFirstAppearance()
    {
        var id = await AddOkAsync(GroupBody(downloadType: 4, order: 1, minFrequency: 0, maxFrequency: 2));

        (await VocabularyAsync(id, "chrono")).Select(w => w.WordId).Should().Equal(1, 2);
    }

    [Fact]
    public async Task Chrono_FollowsReleaseOrderAfterTheFirstDeckIsReparsed()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            await db.DeckWords.Where(dw => dw.DeckId == 1).ExecuteDeleteAsync();
            db.DeckWords.AddRange(new DeckWord { DeckId = 1, WordId = 1, ReadingIndex = 0, Occurrences = 10 },
                                  new DeckWord { DeckId = 1, WordId = 2, ReadingIndex = 0, Occurrences = 5 });
            await db.SaveChangesAsync();
        }

        var id = await AddOkAsync(GroupBody(kind: Franchise, id: _franchiseId, order: 1));

        (await VocabularyAsync(id, "chrono")).Select(w => w.WordId).Should().Equal(1, 2, 3, 4);
        var groupVocabulary = await GetJsonAsync($"/api/media-group/vocabulary?kind={Franchise}&id={_franchiseId}");
        groupVocabulary.GetProperty("data").GetProperty("words").EnumerateArray()
                       .Select(w => w.GetProperty("wordId").GetInt32()).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task TargetCoverage_CountsAgainstTheMergedTotal()
    {
        // 46 merged occurrences; 20 + 12 = 32 passes half.
        var id = await AddOkAsync(GroupBody(downloadType: 5, targetPercentage: 50));

        (await VocabularyAsync(id)).Select(w => w.WordId).Should().Equal(4, 2);
    }

    [Theory]
    [InlineData(Series, SagaId, 3)]
    [InlineData(Line, 1, 2)]
    public async Task PreviewCount_ResolvesTheGroup(int kind, int id, int expected)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks/preview-count")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(GroupBody(kind: kind, id: id, mediaTypes: [(int)MediaType.Anime])));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("total").GetInt32().Should().Be(expected);
    }

    private async Task SetGatheringAsync(string gathering)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new
                                               {
                                                   newCardsPerDay = 10, maxReviewsPerDay = 200, gradingButtons = 4, interleaving = "newFirst",
                                                   reviewFrom = "allTracked", newCardGathering = gathering
                                               }));
        response.EnsureSuccessStatusCode();
    }

    private async Task<List<JsonElement>> NewCardsAsync() =>
        (await GetJsonAsync("/api/srs/study-batch?limit=20")).GetProperty("cards").EnumerateArray()
                                                              .Where(c => c.GetProperty("isNewCard").GetBoolean())
                                                              .ToList();

    [Fact]
    public async Task StudyBatch_GathersNewCardsFromTheGroup()
    {
        await SetGatheringAsync("topDeck");
        await AddOkAsync(GroupBody(excluded: [2]));

        var cards = await NewCardsAsync();

        cards.Select(c => c.GetProperty("wordId").GetInt32()).Should().Equal(4, 1, 2);
        cards.Should().AllSatisfy(c => c.GetProperty("sourceDeckName").GetString().Should().Be("Saga"));
    }

    [Fact]
    public async Task StudyBatch_CrossDeck_RanksGroupWordsWithMediaDecks()
    {
        await SetGatheringAsync("crossDeckFrequency");
        await AddOkAsync(new { deckId = OutsideDeckId, downloadType = 1, order = 3 });
        await AddOkAsync(GroupBody());

        var cards = await NewCardsAsync();

        cards.Select(c => c.GetProperty("wordId").GetInt32()).Should().Equal(5, 4, 2, 1, 3);
        cards.Single(c => c.GetProperty("wordId").GetInt32() == 4).GetProperty("sourceDeckName").GetString().Should().Be("Saga");
    }

    [Fact]
    public async Task Create_ValidatesTheGroupAndItsFilters()
    {
        (await AddAsync(new { deckType = 4, downloadType = 1, order = 3 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AddAsync(new { deckType = 4, groupId = SagaId, downloadType = 1, order = 3 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AddAsync(GroupBody(id: 999))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AddAsync(GroupBody(kind: Franchise, id: 999))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AddAsync(GroupBody(kind: Line, id: 999))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AddAsync(GroupBody(kind: 9))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AddAsync(GroupBody(mediaTypes: [999]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AddAsync(GroupBody(excluded: [OutsideDeckId]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AddAsync(GroupBody(kind: Line, id: 1, excluded: [3]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AddAsync(GroupBody(order: 6))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await AddOkAsync(GroupBody(mediaTypes: [(int)MediaType.Novel, (int)MediaType.Anime]));
        (await AddAsync(GroupBody(mediaTypes: [(int)MediaType.Anime, (int)MediaType.Novel]))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await AddOkAsync(GroupBody(mediaTypes: [(int)MediaType.Anime]));
        await AddOkAsync(GroupBody(kind: Line, id: SagaId, mediaTypes: [(int)MediaType.Anime]));
    }

    [Fact]
    public async Task FranchiseMerge_RepointsTheStudyDeck()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.Decks.AddRange(Deck(5, "Big one", MediaType.Anime, 2010), Deck(6, "Big two", MediaType.Anime, 2011),
                              Deck(7, "Big three", MediaType.Anime, 2012), Deck(8, "Big four", MediaType.Anime, 2013));
            await db.SaveChangesAsync();
            db.DeckRelationships.AddRange(Rel(6, 5), Rel(7, 6), Rel(8, 7));
            await db.SaveChangesAsync();
        }

        var bigFranchiseId = await SyncAsync(5);
        var id = await AddOkAsync(GroupBody(kind: Franchise, id: _franchiseId));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.DeckRelationships.Add(Rel(5, 3));
            await db.SaveChangesAsync();
        }

        (await SyncAsync(1)).Should().Be(bigFranchiseId);

        var row = await StudyDeckRowAsync(id);
        row.GetProperty("groupId").GetInt32().Should().Be(bigFranchiseId);
        row.GetProperty("groupName").GetString().Should().NotBeNull();
        row.GetProperty("totalWords").GetInt32().Should().Be(4);
    }

    [Fact]
    public async Task DeletedSeries_LeavesAnEmptyDeck()
    {
        var id = await AddOkAsync(GroupBody());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            await db.SeriesMembers.ExecuteDeleteAsync();
            await db.Series.ExecuteDeleteAsync();
        }

        var row = await StudyDeckRowAsync(id);
        row.GetProperty("groupName").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("title").GetString().Should().BeEmpty();
        row.GetProperty("totalWords").GetInt32().Should().Be(0);
        (await VocabularyAsync(id)).Should().BeEmpty();
        (await GetJsonAsync("/api/srs/study-batch?limit=20")).GetProperty("cards").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task DeletedFranchise_LeavesAnEmptyDeck()
    {
        var id = await AddOkAsync(GroupBody(kind: Franchise, id: _franchiseId));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            await db.DeckRelationships.ExecuteDeleteAsync();
            await db.SeriesMembers.ExecuteDeleteAsync();
            await scope.ServiceProvider.GetRequiredService<FranchiseSyncRunner>().RunAsync();
        }

        var row = await StudyDeckRowAsync(id);
        row.GetProperty("groupId").GetInt32().Should().Be(_franchiseId);
        row.GetProperty("groupName").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("groupFranchiseId").ValueKind.Should().Be(JsonValueKind.Null);
        row.GetProperty("totalWords").GetInt32().Should().Be(0);
        (await VocabularyAsync(id)).Should().BeEmpty();

        var update = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, $"/api/srs/study-decks/{id}")
                                             .WithUser(TestUsers.UserA)
                                             .WithJsonContent(new { downloadType = 1, order = 3 }));
        update.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
