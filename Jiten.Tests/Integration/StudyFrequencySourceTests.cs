using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
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

        await jitenDb.SaveChangesAsync();
    }

    private async Task ClearUserState()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.UserStudyDecks.ExecuteDeleteAsync();
        await userDb.UserFrequencyLists.ExecuteDeleteAsync();
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
}
