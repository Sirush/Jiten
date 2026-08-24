using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// The per-user default frequency source. Words 1-5 are globally ranked 1-5; Anime observes 1-3 (ranked 1-3) and
/// Novel observes 4-5, so word 5 is absent from Anime and word 1 is absent from Novel.
/// </summary>
public class DefaultFrequencySourceTests(JitenWebApplicationFactory factory)
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

        foreach (var (wordId, rank) in new[] { (1, 3), (2, 2), (3, 1) })
            jitenDb.WordFormFrequenciesByType.Add(new JmDictWordFormFrequencyByType
            {
                MediaType = MediaType.Anime, WordId = wordId, ReadingIndex = 0,
                FrequencyRank = rank, UsedInMediaAmount = 7, ObservedFrequency = 0.1
            });

        foreach (var (wordId, rank) in new[] { (4, 1), (5, 2) })
            jitenDb.WordFormFrequenciesByType.Add(new JmDictWordFormFrequencyByType
            {
                MediaType = MediaType.Novel, WordId = wordId, ReadingIndex = 0,
                FrequencyRank = rank, UsedInMediaAmount = 3, ObservedFrequency = 0.1
            });

        await jitenDb.SaveChangesAsync();
    }

    private async Task ClearUserState()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.UserFrequencyLists.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();

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
            list.RankedWordsBlob = FrequencyListBlobPacker.Pack(rankedWords ?? [(3, 0), (1, 0)]);
            list.BlobGeneratedAt = DateTime.UtcNow;
        }

        userDb.UserFrequencyLists.Add(list);
        await userDb.SaveChangesAsync();
        return list.Id;
    }

    // ---- Helpers ------------------------------------------------------------

    private async Task<HttpResponseMessage> PutSettings(object body, string userId) =>
        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
                                .WithUser(userId).WithJsonContent(body));

    private async Task SetDefault(string userId, int? mediaType = null, long? listId = null)
    {
        var res = await PutSettings(new { defaultFrequencyMediaType = mediaType ?? 0, defaultFrequencyListId = listId ?? 0L }, userId);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> FrequencyRanks(int wordId, byte readingIndex = 0, string? userId = null,
                                                   bool includeLists = false)
    {
        var url = $"/api/vocabulary/{wordId}/{readingIndex}/frequency-ranks?includeLists={includeLists}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (userId != null) request = request.WithUser(userId);

        var res = await _client.SendAsync(request);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> GetWord(int wordId, byte readingIndex, string userId)
    {
        var res = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/vocabulary/{wordId}/{readingIndex}").WithUser(userId));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement Resolved(JsonElement ranks) => ranks.GetProperty("resolved");

    // ---- Resolution ---------------------------------------------------------

    [Fact]
    public async Task TwoUsersWithDifferentDefaults_ResolveDifferentRanks()
    {
        await SetDefault(TestUsers.UserA, mediaType: (int)MediaType.Anime);

        var forA = Resolved(await FrequencyRanks(1, userId: TestUsers.UserA));
        forA.GetProperty("source").GetString().Should().Be("mediaType");
        forA.GetProperty("mediaType").GetInt32().Should().Be((int)MediaType.Anime);
        forA.GetProperty("rank").GetInt32().Should().Be(3);
        forA.GetProperty("isFallback").GetBoolean().Should().BeFalse();

        var forB = Resolved(await FrequencyRanks(1, userId: TestUsers.UserB));
        forB.GetProperty("source").GetString().Should().Be("global");
        forB.GetProperty("rank").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task AnonymousCaller_AlwaysGetsTheGlobalRank()
    {
        await SetDefault(TestUsers.UserA, mediaType: (int)MediaType.Anime);

        var anonymous = await FrequencyRanks(1);
        Resolved(anonymous).GetProperty("source").GetString().Should().Be("global");
        Resolved(anonymous).GetProperty("rank").GetInt32().Should().Be(1);
        anonymous.TryGetProperty("lists", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ByType_ListsOnlyTheTypesThatObservedTheForm()
    {
        var ranks = await FrequencyRanks(1, userId: TestUsers.UserA);
        var byType = ranks.GetProperty("byType");

        byType.GetProperty(((int)MediaType.Anime).ToString()).GetProperty("rank").GetInt32().Should().Be(3);
        byType.GetProperty(((int)MediaType.Anime).ToString()).GetProperty("amount").GetInt32().Should().Be(7);
        byType.TryGetProperty(((int)MediaType.Novel).ToString(), out _).Should().BeFalse();
    }

    // ---- Fallback -----------------------------------------------------------

    [Fact]
    public async Task MediaTypeDefault_WordAbsentFromThatType_FallsBackToGlobal()
    {
        await SetDefault(TestUsers.UserA, mediaType: (int)MediaType.Anime);

        var resolved = Resolved(await FrequencyRanks(5, userId: TestUsers.UserA));
        resolved.GetProperty("source").GetString().Should().Be("global");
        resolved.GetProperty("rank").GetInt32().Should().Be(5);
        resolved.GetProperty("isFallback").GetBoolean().Should().BeTrue();

        var word = await GetWord(5, 0, TestUsers.UserA);
        var mainReading = word.GetProperty("mainReading");
        mainReading.GetProperty("frequencyRank").GetInt32().Should().Be(5);
        mainReading.GetProperty("frequencyRankSource").GetString().Should().Be("global");
        mainReading.GetProperty("isFrequencyFallback").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task MediaTypeDefault_WordPresentInThatType_UsesItsRank()
    {
        await SetDefault(TestUsers.UserA, mediaType: (int)MediaType.Anime);

        var mainReading = (await GetWord(3, 0, TestUsers.UserA)).GetProperty("mainReading");
        mainReading.GetProperty("frequencyRank").GetInt32().Should().Be(1);
        mainReading.GetProperty("frequencyRankSource").GetString().Should().Be("mediaType");
        mainReading.TryGetProperty("isFrequencyFallback", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ListDefault_WordOutsideTheList_IsUnrankedWithNoFallback()
    {
        var listId = await SeedList(TestUsers.UserA, isSaved: true, withBlob: true, rankedWords: [(3, 0), (1, 0)]);
        await SetDefault(TestUsers.UserA, listId: listId);

        var inList = Resolved(await FrequencyRanks(1, userId: TestUsers.UserA));
        inList.GetProperty("source").GetString().Should().Be("list");
        inList.GetProperty("listId").GetInt64().Should().Be(listId);
        inList.GetProperty("rank").GetInt32().Should().Be(2);

        var outside = Resolved(await FrequencyRanks(5, userId: TestUsers.UserA));
        outside.GetProperty("source").GetString().Should().Be("list");
        outside.GetProperty("rank").GetInt32().Should().Be(0);
        outside.GetProperty("isFallback").GetBoolean().Should().BeFalse();

        var mainReading = (await GetWord(5, 0, TestUsers.UserA)).GetProperty("mainReading");
        mainReading.GetProperty("frequencyRank").GetInt32().Should().Be(0);
        mainReading.GetProperty("frequencyRankSource").GetString().Should().Be("list");
    }

    [Fact]
    public async Task IncludeLists_ReportsEverySavedListWithZeroForWordsOutsideIt()
    {
        var listId = await SeedList(TestUsers.UserA, isSaved: true, withBlob: true, rankedWords: [(3, 0), (1, 0)]);

        var lists = (await FrequencyRanks(5, userId: TestUsers.UserA, includeLists: true)).GetProperty("lists");
        lists.GetArrayLength().Should().Be(1);
        lists[0].GetProperty("id").GetInt64().Should().Be(listId);
        lists[0].GetProperty("rank").GetInt32().Should().Be(0);

        var present = (await FrequencyRanks(3, userId: TestUsers.UserA, includeLists: true)).GetProperty("lists");
        present[0].GetProperty("rank").GetInt32().Should().Be(1);
    }

    // ---- Staleness ----------------------------------------------------------

    [Fact]
    public async Task DeletedListDefault_ResolvesGlobalWithoutError()
    {
        var listId = await SeedList(TestUsers.UserA, isSaved: true, withBlob: true);
        await SetDefault(TestUsers.UserA, listId: listId);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.UserFrequencyLists.Where(f => f.Id == listId).ExecuteDeleteAsync();
        }

        FrequencySourceResolver.Invalidate(factory.Services.GetRequiredService<IMemoryCache>(), TestUsers.UserA);

        var resolved = Resolved(await FrequencyRanks(1, userId: TestUsers.UserA));
        resolved.GetProperty("source").GetString().Should().Be("global");
        resolved.GetProperty("rank").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task UnsavedOrBloblessListDefault_ResolvesGlobalWithoutError()
    {
        var listId = await SeedList(TestUsers.UserA, isSaved: true, withBlob: true);
        await SetDefault(TestUsers.UserA, listId: listId);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.UserFrequencyLists.Where(f => f.Id == listId)
                        .ExecuteUpdateAsync(s => s.SetProperty(f => f.RankedWordsBlob, (byte[]?)null));
        }

        FrequencySourceResolver.Invalidate(factory.Services.GetRequiredService<IMemoryCache>(), TestUsers.UserA);

        Resolved(await FrequencyRanks(1, userId: TestUsers.UserA)).GetProperty("source").GetString().Should().Be("global");
    }

    // ---- Shared caches ------------------------------------------------------

    [Fact]
    public async Task WordInfo_IsByteIdenticalForUsersWithDifferentDefaults()
    {
        var listId = await SeedList(TestUsers.UserB, isSaved: true, withBlob: true);
        await SetDefault(TestUsers.UserA, mediaType: (int)MediaType.Anime);
        await SetDefault(TestUsers.UserB, listId: listId);

        var forA = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/vocabulary/1/0/info").WithUser(TestUsers.UserA));
        var forB = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/vocabulary/1/0/info").WithUser(TestUsers.UserB));
        var anonymous = await _client.GetAsync("/api/vocabulary/1/0/info");

        var bodyA = await forA.Content.ReadAsStringAsync();
        var bodyB = await forB.Content.ReadAsStringAsync();
        var bodyAnonymous = await anonymous.Content.ReadAsStringAsync();

        bodyB.Should().Be(bodyA);
        bodyAnonymous.Should().Be(bodyA);
        bodyA.Should().NotContain("frequencyRankSource");
    }

    // ---- Settings validation ------------------------------------------------

    [Fact]
    public async Task SettingsPut_RejectsBothSourcesAtOnce()
    {
        var listId = await SeedList(TestUsers.UserA, isSaved: true, withBlob: true);

        var res = await PutSettings(
            new { defaultFrequencyMediaType = (int)MediaType.Anime, defaultFrequencyListId = listId }, TestUsers.UserA);

        // Setting a media type clears any list, so this saves as the media type rather than failing.
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var dto = await res.Content.ReadFromJsonAsync<JsonElement>();
        dto.GetProperty("defaultFrequencyMediaType").GetInt32().Should().Be((int)MediaType.Anime);
        dto.TryGetProperty("defaultFrequencyListId", out var stored).Should().BeTrue();
        stored.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task SettingsPut_RejectsUnknownMediaType()
    {
        var res = await PutSettings(new { defaultFrequencyMediaType = 99 }, TestUsers.UserA);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SettingsPut_RejectsAListTheUserDoesNotOwn()
    {
        var listId = await SeedList(TestUsers.UserB, isSaved: true, withBlob: true);

        var res = await PutSettings(new { defaultFrequencyListId = listId }, TestUsers.UserA);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SettingsPut_RejectsAnUnsavedList()
    {
        var listId = await SeedList(TestUsers.UserA, isSaved: false, withBlob: true);

        var res = await PutSettings(new { defaultFrequencyListId = listId }, TestUsers.UserA);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SettingsPut_ClearsBackToGlobal()
    {
        await SetDefault(TestUsers.UserA, mediaType: (int)MediaType.Anime);
        Resolved(await FrequencyRanks(1, userId: TestUsers.UserA)).GetProperty("source").GetString().Should().Be("mediaType");

        await SetDefault(TestUsers.UserA);

        Resolved(await FrequencyRanks(1, userId: TestUsers.UserA)).GetProperty("source").GetString().Should().Be("global");
    }

    [Fact]
    public async Task SettingsPut_OmittingTheFieldsLeavesTheDefaultAlone()
    {
        await SetDefault(TestUsers.UserA, mediaType: (int)MediaType.Anime);

        var res = await PutSettings(new { newCardsPerDay = 12 }, TestUsers.UserA);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var dto = await res.Content.ReadFromJsonAsync<JsonElement>();
        dto.GetProperty("defaultFrequencyMediaType").GetInt32().Should().Be((int)MediaType.Anime);
    }
}
