using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Enums;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// Frequency-scoped study decks: per-media-type rankings and custom-list blobs feeding StudyDeckType.GlobalDynamic.
/// Words 1-5 are globally ranked 1-5; Anime observes 1-3 and Novel observes 4-5.
/// </summary>
public class StudyFrequencySourceTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        await SeedVocabulary();
        await ClearUserState();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Seeding ------------------------------------------------------------

    private async Task SeedVocabulary()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        await jitenDb.WordFormFrequenciesByType.ExecuteDeleteAsync();
        await jitenDb.WordFrequenciesByType.ExecuteDeleteAsync();
        await jitenDb.WordFormFrequencies.ExecuteDeleteAsync();
        await jitenDb.WordForms.ExecuteDeleteAsync();
        await jitenDb.Definitions.ExecuteDeleteAsync();
        await jitenDb.JMDictWords.ExecuteDeleteAsync();

        for (var i = 1; i <= 5; i++)
        {
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = i, PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.Add(new JmDictWordForm
            {
                WordId = i, ReadingIndex = 0, Text = $"言葉{i}", RubyText = $"言葉{i}", FormType = JmDictFormType.KanjiForm
            });
            jitenDb.Definitions.Add(new JmDictDefinition
            {
                WordId = i, SenseIndex = 0, EnglishMeanings = [$"meaning{i}"], PartsOfSpeech = ["noun"]
            });
            jitenDb.WordFormFrequencies.Add(new JmDictWordFormFrequency
            {
                WordId = i, ReadingIndex = 0, FrequencyRank = i, UsedInMediaAmount = 1, ObservedFrequency = 0.1
            });
        }

        foreach (var (wordId, rank) in new[] { (1, 1), (2, 2), (3, 3) })
            jitenDb.WordFormFrequenciesByType.Add(new JmDictWordFormFrequencyByType
            {
                MediaType = MediaType.Anime, WordId = wordId, ReadingIndex = 0,
                FrequencyRank = rank, UsedInMediaAmount = 1, ObservedFrequency = 0.1
            });

        foreach (var (wordId, rank) in new[] { (4, 1), (5, 2) })
            jitenDb.WordFormFrequenciesByType.Add(new JmDictWordFormFrequencyByType
            {
                MediaType = MediaType.Novel, WordId = wordId, ReadingIndex = 0,
                FrequencyRank = rank, UsedInMediaAmount = 1, ObservedFrequency = 0.1
            });

        await jitenDb.DeckWords.Where(dw => dw.DeckId == MediaDeckId).ExecuteDeleteAsync();
        await jitenDb.Decks.Where(d => d.DeckId == MediaDeckId).ExecuteDeleteAsync();
        var deck = new Deck
        {
            DeckId = MediaDeckId, OriginalTitle = "Media", MediaType = MediaType.Anime, CreationDate = DateTime.UtcNow,
            CharacterCount = 50, WordCount = 15, UniqueWordCount = 5
        };
        jitenDb.Decks.Add(deck);
        for (var i = 1; i <= 5; i++)
            jitenDb.DeckWords.Add(new DeckWord { Deck = deck, WordId = i, ReadingIndex = 0, Occurrences = i });

        await jitenDb.SaveChangesAsync();
    }

    private const int MediaDeckId = 7401;

    private async Task ClearUserState()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.UserStudyDecks.ExecuteDeleteAsync();
        await userDb.UserFrequencyLists.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();

        var cache = factory.Services.GetRequiredService<IMemoryCache>();
        foreach (var userId in new[] { TestUsers.UserA, TestUsers.UserB })
            FrequencySourceResolver.Invalidate(cache, userId);
    }

    private async Task<long> SeedList(string userId, bool isSaved, bool withBlob,
        List<(int WordId, byte ReadingIndex)>? rankedWords = null)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var list = new UserFrequencyList
        {
            UserId = userId,
            Name = "My list",
            Mode = FrequencyListMode.Filters,
            IsSaved = isSaved,
            Status = FrequencyListStatus.Ready,
            GeneratedAt = DateTime.UtcNow
        };

        if (withBlob)
        {
            list.RankedWordsBlob = FrequencyListBlobPacker.Pack(rankedWords ?? [(1, 0), (3, 0), (5, 0)]);
            list.BlobGeneratedAt = DateTime.UtcNow;
        }

        userDb.UserFrequencyLists.Add(list);
        await userDb.SaveChangesAsync();
        return list.Id;
    }

    private async Task MakeFull(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var user = await userDb.Users.FirstAsync(u => u.Id == userId);
        user.AdminPremiumOverride = true;
        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(userId);
    }

    // ---- Helpers ------------------------------------------------------------

    private async Task<HttpResponseMessage> AddDeck(object body, string userId = TestUsers.UserA) =>
        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
                                .WithUser(userId).WithJsonContent(body));

    private static object GlobalDynamicBody(string name, int? mediaType = null, long? listId = null,
        int minRank = 1, int maxRank = 10) => new
    {
        deckType = (int)StudyDeckType.GlobalDynamic,
        name,
        order = 2,
        minGlobalFrequency = minRank,
        maxGlobalFrequency = maxRank,
        frequencyMediaType = mediaType,
        frequencyListId = listId
    };

    private async Task<int> AddDeckOk(object body, string userId = TestUsers.UserA)
    {
        var res = await AddDeck(body, userId);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var dto = await res.Content.ReadFromJsonAsync<JsonElement>();
        return dto.GetProperty("userStudyDeckId").GetInt32();
    }

    private async Task<List<int>> VocabularyWordIds(int studyDeckId, string userId = TestUsers.UserA)
    {
        var res = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/srs/study-decks/{studyDeckId}/vocabulary").WithUser(userId));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").EnumerateArray().Select(w => w.GetProperty("wordId").GetInt32()).ToList();
    }

    private async Task<int> PreviewTotal(object body, string userId = TestUsers.UserA)
    {
        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks/preview-count")
                                          .WithUser(userId).WithJsonContent(body));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var dto = await res.Content.ReadFromJsonAsync<JsonElement>();
        return dto.GetProperty("total").GetInt32();
    }

    // ---- Media type scope ---------------------------------------------------

    [Fact]
    public async Task MediaTypeScopedDeck_ResolvesOnlyThatTypesWords()
    {
        var animeDeck = await AddDeckOk(GlobalDynamicBody("Anime", mediaType: (int)MediaType.Anime));

        (await VocabularyWordIds(animeDeck)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task DifferentMediaTypes_ResolveDisjointWords()
    {
        var animeDeck = await AddDeckOk(GlobalDynamicBody("Anime", mediaType: (int)MediaType.Anime));
        var novelDeck = await AddDeckOk(GlobalDynamicBody("Novel", mediaType: (int)MediaType.Novel));

        (await VocabularyWordIds(animeDeck)).Should().Equal(1, 2, 3);
        (await VocabularyWordIds(novelDeck)).Should().Equal(4, 5);
    }

    [Fact]
    public async Task UnscopedDeck_StillResolvesTheGlobalRanking()
    {
        var globalDeck = await AddDeckOk(GlobalDynamicBody("Global"));

        (await VocabularyWordIds(globalDeck)).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task Overview_CountsScopedAndUnscopedDecksSeparately()
    {
        var animeDeck = await AddDeckOk(GlobalDynamicBody("Anime", mediaType: (int)MediaType.Anime));
        var globalDeck = await AddDeckOk(GlobalDynamicBody("Global"));

        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-decks").WithUser(TestUsers.UserA));
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var decks = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .ToDictionary(d => d.GetProperty("userStudyDeckId").GetInt32());

        decks[animeDeck].GetProperty("totalWords").GetInt32().Should().Be(3);
        decks[animeDeck].GetProperty("frequencySourceName").GetString().Should().Be("Anime");
        decks[globalDeck].GetProperty("totalWords").GetInt32().Should().Be(5);
        decks[globalDeck].GetProperty("frequencySourceName").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task PreviewCount_HonoursMediaTypeScope()
    {
        (await PreviewTotal(GlobalDynamicBody("Anime", mediaType: (int)MediaType.Anime))).Should().Be(3);
        (await PreviewTotal(GlobalDynamicBody("Global"))).Should().Be(5);
    }

    // ---- Validation ---------------------------------------------------------

    [Fact]
    public async Task BothSourcesSet_IsRejected()
    {
        var listId = await SeedList(TestUsers.UserA, isSaved: true, withBlob: true);

        var res = await AddDeck(GlobalDynamicBody("Both", mediaType: (int)MediaType.Anime, listId: listId));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnknownMediaType_IsRejected()
    {
        var res = await AddDeck(GlobalDynamicBody("Bad", mediaType: 99));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListOwnedByAnotherUser_IsRejected()
    {
        var listId = await SeedList(TestUsers.UserB, isSaved: true, withBlob: true);

        var res = await AddDeck(GlobalDynamicBody("Someone else's", listId: listId));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnsavedList_IsRejected()
    {
        var listId = await SeedList(TestUsers.UserA, isSaved: false, withBlob: true);

        var res = await AddDeck(GlobalDynamicBody("Transient", listId: listId));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SavedListWithoutBlob_Returns409()
    {
        var listId = await SeedList(TestUsers.UserA, isSaved: true, withBlob: false);

        var res = await AddDeck(GlobalDynamicBody("Not ready", listId: listId));

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---- Custom list scope --------------------------------------------------

    [Fact]
    public async Task ListScopedDeck_ResolvesBlobEntriesInRankOrder()
    {
        var listId = await SeedList(TestUsers.UserA, isSaved: true, withBlob: true);
        var deckId = await AddDeckOk(GlobalDynamicBody("List", listId: listId, minRank: 1, maxRank: 10));

        (await VocabularyWordIds(deckId)).Should().Equal(1, 3, 5);
    }

    [Fact]
    public async Task ListScopedDeck_HonoursRankWindow()
    {
        var listId = await SeedList(TestUsers.UserA, isSaved: true, withBlob: true);
        var deckId = await AddDeckOk(GlobalDynamicBody("List", listId: listId, minRank: 2, maxRank: 3));

        (await VocabularyWordIds(deckId)).Should().Equal(3, 5);
    }

    [Fact]
    public async Task ListScopedDeck_MembershipKeysMatchResolvedWords()
    {
        var listId = await SeedList(TestUsers.UserA, isSaved: true, withBlob: true);
        var scope = new FrequencyScope(null, listId);

        using var serviceScope = factory.Services.CreateScope();
        var resolver = new DeckWordResolver(
            serviceScope.ServiceProvider.GetRequiredService<JitenDbContext>(),
            serviceScope.ServiceProvider.GetRequiredService<UserDbContext>(),
            serviceScope.ServiceProvider.GetRequiredService<ICurrentUserService>(),
            serviceScope.ServiceProvider.GetRequiredService<IWordFormSiblingCache>(),
            serviceScope.ServiceProvider.GetRequiredService<IMemoryCache>());

        var keys = await resolver.GetGlobalDynamicWordKeysForWordIds(2, 3, null, [1, 2, 3, 4, 5], false, scope);

        keys.Should().BeEquivalentTo([(3L << 8) | 0, (5L << 8) | 0]);
    }

    [Fact]
    public async Task DeletingAListAStudyDeckUsesIsBlocked()
    {
        await MakeFull(TestUsers.UserA);
        var listId = await SeedList(TestUsers.UserA, isSaved: true, withBlob: true);
        await AddDeckOk(GlobalDynamicBody("List", listId: listId));

        var res = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"/api/frequency-lists/{listId}").WithUser(TestUsers.UserA));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- Account default drives frequency order -----------------------------

    private async Task SetDefault(string userId, int? mediaType = null, long? listId = null)
    {
        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
                                          .WithUser(userId)
                                          .WithJsonContent(new
                                          {
                                              defaultFrequencyMediaType = mediaType ?? 0, defaultFrequencyListId = listId ?? 0L,
                                              interleaving = "NewFirst"
                                          }));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private static object MediaDeckBody(int order = 2, int downloadType = 1, int minFrequency = 0, int maxFrequency = 0) => new
    {
        deckType = (int)StudyDeckType.MediaDeck, deckId = MediaDeckId, downloadType, order, minFrequency, maxFrequency
    };

    private async Task<int> AddWordListDeck(params int[] wordIdsInImportOrder)
    {
        var deckId = await AddDeckOk(new { deckType = (int)StudyDeckType.StaticWordList, name = "List", downloadType = 1, order = 2 });
        var words = wordIdsInImportOrder.Select(id => new { wordId = id, readingIndex = 0, occurrences = 1 }).ToArray();
        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/words/batch")
                                          .WithUser(TestUsers.UserA).WithJsonContent(new { words }));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return deckId;
    }

    private async Task<List<int>> NewCardOrder(string userId = TestUsers.UserA)
    {
        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-batch?limit=20").WithUser(userId));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("wordId").GetInt32()).ToList();
    }

    private async Task<List<int>> VocabularyByRank(int studyDeckId, string userId = TestUsers.UserA,
        SortOrder sortOrder = SortOrder.Ascending)
    {
        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get,
                $"/api/srs/study-decks/{studyDeckId}/vocabulary?sortBy=globalFreq&sortOrder={(int)sortOrder}")
            .WithUser(userId));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").EnumerateArray().Select(w => w.GetProperty("wordId").GetInt32()).ToList();
    }

    [Fact]
    public async Task MediaDeckInFrequencyOrder_GlobalDefault_KeepsTheGlobalOrder()
    {
        var deckId = await AddDeckOk(MediaDeckBody());

        (await NewCardOrder()).Should().Equal(1, 2, 3, 4, 5);
        (await VocabularyByRank(deckId)).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task MediaDeckInFrequencyOrder_NovelDefault_ServesNovelRanksThenUnrankedByGlobalRank()
    {
        await SetDefault(TestUsers.UserA, mediaType: (int)MediaType.Novel);
        var deckId = await AddDeckOk(MediaDeckBody());

        (await NewCardOrder()).Should().Equal(4, 5, 1, 2, 3);
        (await VocabularyByRank(deckId)).Should().Equal(4, 5, 1, 2, 3);
    }

    [Fact]
    public async Task MediaDeckInFrequencyOrder_ListDefault_PutsOutOfListWordsLast()
    {
        await MakeFull(TestUsers.UserA);
        var listId = await SeedList(TestUsers.UserA, isSaved: true, withBlob: true, rankedWords: [(3, 0), (1, 0)]);
        await SetDefault(TestUsers.UserA, listId: listId);
        var deckId = await AddDeckOk(MediaDeckBody());

        (await NewCardOrder()).Should().Equal(3, 1, 2, 4, 5);
        (await VocabularyByRank(deckId)).Should().Equal(3, 1, 2, 4, 5);
    }

    [Fact]
    public async Task MediaDeckRankBand_StaysGlobal_WhileOrderFollowsTheDefault()
    {
        await SetDefault(TestUsers.UserA, mediaType: (int)MediaType.Anime);
        // A band read from Anime would hold only words 2 and 3; word 4 proves the band is global.
        var deckId = await AddDeckOk(MediaDeckBody(downloadType: 2, minFrequency: 2, maxFrequency: 4));

        (await NewCardOrder()).Should().Equal(2, 3, 4);

        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-decks").WithUser(TestUsers.UserA));
        var deck = (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
                                                                       .Single(d => d.GetProperty("userStudyDeckId").GetInt32() == deckId);
        deck.GetProperty("totalWords").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task WordListInFrequencyOrder_GlobalDefault_KeepsTheGlobalOrder()
    {
        var deckId = await AddWordListDeck(5, 3, 1, 4, 2);

        (await NewCardOrder()).Should().Equal(1, 2, 3, 4, 5);
        (await VocabularyByRank(deckId)).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task WordListInFrequencyOrder_NovelDefault_ServesNovelRanksThenUnrankedByGlobalRank()
    {
        await SetDefault(TestUsers.UserA, mediaType: (int)MediaType.Novel);
        var deckId = await AddWordListDeck(5, 3, 1, 4, 2);

        (await NewCardOrder()).Should().Equal(4, 5, 1, 2, 3);
        (await VocabularyByRank(deckId)).Should().Equal(4, 5, 1, 2, 3);
    }

    [Fact]
    public async Task VocabularyRankSort_Descending_KeepsUnrankedWordsLast()
    {
        await SetDefault(TestUsers.UserA, mediaType: (int)MediaType.Novel);
        var mediaDeckId = await AddDeckOk(MediaDeckBody());
        var wordListId = await AddWordListDeck(5, 3, 1, 4, 2);

        (await VocabularyByRank(mediaDeckId, sortOrder: SortOrder.Descending)).Should().Equal(5, 4, 3, 2, 1);
        (await VocabularyByRank(wordListId, sortOrder: SortOrder.Descending)).Should().Equal(5, 4, 3, 2, 1);
    }

    [Fact]
    public async Task WordListInFrequencyOrder_ListDefault_PutsOutOfListWordsLast()
    {
        await MakeFull(TestUsers.UserA);
        var listId = await SeedList(TestUsers.UserA, isSaved: true, withBlob: true, rankedWords: [(3, 0), (1, 0)]);
        await SetDefault(TestUsers.UserA, listId: listId);
        await AddWordListDeck(5, 3, 1, 4, 2);

        (await NewCardOrder()).Should().Equal(3, 1, 2, 4, 5);
    }

    [Fact]
    public async Task FrequencyDeck_IgnoresTheAccountDefault()
    {
        await SetDefault(TestUsers.UserA, mediaType: (int)MediaType.Novel);
        var deckId = await AddDeckOk(GlobalDynamicBody("Global"));

        (await NewCardOrder()).Should().Equal(1, 2, 3, 4, 5);
        (await VocabularyWordIds(deckId)).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task DefaultOnlyAppliesToItsOwner()
    {
        await SetDefault(TestUsers.UserA, mediaType: (int)MediaType.Novel);
        await AddDeckOk(MediaDeckBody(), TestUsers.UserB);

        (await NewCardOrder(TestUsers.UserB)).Should().Equal(1, 2, 3, 4, 5);
    }
}
