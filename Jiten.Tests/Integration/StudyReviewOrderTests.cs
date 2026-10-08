using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>Words 1-5 are globally ranked 1-5; every test studies UserA's reviews with no new cards.</summary>
public class StudyReviewOrderTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        await jitenDb.WordFormFrequenciesByType.ExecuteDeleteAsync();
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
        await jitenDb.SaveChangesAsync();

        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();
        await userDb.UserStudyDecks.ExecuteDeleteAsync();
        await userDb.UserFrequencyLists.ExecuteDeleteAsync();
        FrequencySourceResolver.Invalidate(scope.ServiceProvider.GetRequiredService<IMemoryCache>(), TestUsers.UserA);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task AddReview(int wordId, double? stability = 10, double? difficulty = 5, int lastReviewDaysAgo = 20)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTime.UtcNow;
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, 0, state: FsrsState.Review,
            stability: stability, difficulty: difficulty, due: now.AddDays(-1), lastReview: now.AddDays(-lastReviewDaysAgo)));
        await userDb.SaveChangesAsync();
    }

    private async Task AddDueLearningCard(int wordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTime.UtcNow;
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, 0, state: FsrsState.Learning, step: 0,
            stability: 1, difficulty: 5, due: now.AddMinutes(-1), lastReview: now.AddMinutes(-6)));
        await userDb.SaveChangesAsync();
    }

    private async Task<long> SeedList(List<(int WordId, byte ReadingIndex)> rankedWords)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var list = new UserFrequencyList
        {
            UserId = TestUsers.UserA,
            Name = "My list",
            Mode = FrequencyListMode.Filters,
            IsSaved = true,
            Status = FrequencyListStatus.Ready,
            GeneratedAt = DateTime.UtcNow,
            RankedWordsBlob = FrequencyListBlobPacker.Pack(rankedWords),
            BlobGeneratedAt = DateTime.UtcNow
        };
        userDb.UserFrequencyLists.Add(list);
        await userDb.SaveChangesAsync();
        return list.Id;
    }

    private async Task<HttpResponseMessage> PutSettings(object body) =>
        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(body));

    private async Task SetOrder(string order, long? defaultFrequencyListId = null)
    {
        var response = await PutSettings(new
        {
            newCardsPerDay = 0, maxReviewsPerDay = 100, gradingButtons = 4, reviewSortOrder = order,
            defaultFrequencyListId
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<List<JsonElement>> BatchCards()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-batch?limit=20")
            .WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("cards").EnumerateArray().ToList();
    }

    private async Task<List<int>> BatchWordIds() =>
        (await BatchCards()).Select(c => c.GetProperty("wordId").GetInt32()).ToList();

    [Fact]
    public async Task RetrievabilityOrders_AreReverses()
    {
        await AddReview(1, stability: 5);
        await AddReview(2, stability: 50);

        await SetOrder("RetrievabilityAscending");
        (await BatchWordIds()).Should().Equal(1, 2);

        await SetOrder("RetrievabilityDescending");
        (await BatchWordIds()).Should().Equal(2, 1);
    }

    [Fact]
    public async Task CardsWithoutMemoryState_CountAsMostForgotten()
    {
        await AddReview(1, stability: 5);
        await AddReview(2, stability: 50);
        await AddReview(3, stability: null);

        await SetOrder("RetrievabilityAscending");
        (await BatchWordIds()).Should().Equal(3, 1, 2);

        await SetOrder("RetrievabilityDescending");
        (await BatchWordIds()).Should().Equal(2, 1, 3);
    }

    [Fact]
    public async Task DifficultyOrders_AreReverses_WithUnratedCardsLast()
    {
        await AddReview(1, difficulty: 8);
        await AddReview(2, difficulty: 2);
        await AddReview(3, difficulty: null);

        await SetOrder("DifficultyDescending");
        (await BatchWordIds()).Should().Equal(1, 2, 3);

        await SetOrder("DifficultyAscending");
        (await BatchWordIds()).Should().Equal(2, 1, 3);
    }

    [Fact]
    public async Task FrequencyOrders_UseTheGlobalRankingByDefault()
    {
        foreach (var wordId in new[] { 3, 1, 5, 2, 4 })
            await AddReview(wordId);

        await SetOrder("FrequencyRankAscending");
        (await BatchWordIds()).Should().Equal(1, 2, 3, 4, 5);

        await SetOrder("FrequencyRankDescending");
        (await BatchWordIds()).Should().Equal(5, 4, 3, 2, 1);
    }

    [Fact]
    public async Task FrequencyOrders_FollowTheUsersDefaultSource_WithOffListWordsLast()
    {
        // List order reverses the global one for the ranked words: 5 → 1, 3 → 2, 1 → 3; words 2 and 4 are off the list.
        var listId = await SeedList([(5, 0), (3, 0), (1, 0)]);
        for (var wordId = 1; wordId <= 5; wordId++)
            await AddReview(wordId, stability: 10 * wordId);

        await SetOrder("FrequencyRankAscending", listId);
        var cards = await BatchCards();
        cards.Select(c => c.GetProperty("wordId").GetInt32()).Take(3).Should().Equal(5, 3, 1);
        cards.Select(c => c.GetProperty("wordId").GetInt32()).Skip(3).Should().BeEquivalentTo([2, 4]);
        cards.Take(3).Select(c => c.GetProperty("frequencyRank").GetInt32()).Should().Equal(1, 2, 3);

        await SetOrder("FrequencyRankDescending", listId);
        var reversed = await BatchWordIds();
        reversed.Take(3).Should().Equal(1, 3, 5);
        reversed.Skip(3).Should().BeEquivalentTo([2, 4]);
    }

    [Theory]
    [InlineData("RetrievabilityAscending")]
    [InlineData("RetrievabilityDescending")]
    [InlineData("DifficultyDescending")]
    [InlineData("DifficultyAscending")]
    [InlineData("FrequencyRankAscending")]
    [InlineData("FrequencyRankDescending")]
    [InlineData("Random")]
    public async Task LearningCardsComeFirst_UnderEveryOrder(string order)
    {
        await AddReview(1, stability: 5);
        await AddReview(2, stability: 50);
        await AddDueLearningCard(5);

        await SetOrder(order);
        var wordIds = await BatchWordIds();
        wordIds.Should().HaveCount(3);
        wordIds[0].Should().Be(5);
    }

    [Fact]
    public async Task Setting_RoundTrips_AndFallsBackToDefaultWhenOmitted()
    {
        await SetOrder("DifficultyDescending");

        var get = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-settings").WithUser(TestUsers.UserA));
        var stored = await get.Content.ReadFromJsonAsync<JsonElement>();
        stored.GetProperty("reviewSortOrder").GetString().Should().Be("DifficultyDescending");

        (await PutSettings(new { newCardsPerDay = 7, maxReviewsPerDay = 100, gradingButtons = 4 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        get = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-settings").WithUser(TestUsers.UserA));
        stored = await get.Content.ReadFromJsonAsync<JsonElement>();
        stored.GetProperty("reviewSortOrder").GetString().Should().Be("RetrievabilityAscending");
        stored.GetProperty("newCardsPerDay").GetInt32().Should().Be(7);
    }

    [Fact]
    public async Task UnknownOrder_IsRejected()
    {
        (await PutSettings(new { newCardsPerDay = 0, maxReviewsPerDay = 100, gradingButtons = 4, reviewSortOrder = "Alphabetical" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
