using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class MediaDeckCoverageFilterTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Word totals: 1 = 75, 2 = 45, 3 = 120 (capped to 100). Unique totals: 1 = 15, 2 = 90, 3 = 35.</summary>
    private async Task SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        db.Decks.AddRange(
            new Deck { DeckId = 1, OriginalTitle = "Deck One", MediaType = MediaType.Anime, Difficulty = 2.0f },
            new Deck { DeckId = 2, OriginalTitle = "Deck Two", MediaType = MediaType.Anime, Difficulty = 2.0f },
            new Deck { DeckId = 3, OriginalTitle = "Deck Three", MediaType = MediaType.Anime, Difficulty = 2.0f });
        await db.SaveChangesAsync();

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.UserCoverageChunks.ExecuteDeleteAsync();

        userDb.UserCoverageChunks.AddRange(
            Chunk(UserCoverageMetric.MatureCoverage, 40, 40, 80),
            Chunk(UserCoverageMetric.YoungCoverage, 35, 5, 40),
            Chunk(UserCoverageMetric.MatureUniqueCoverage, 10, 70, 30),
            Chunk(UserCoverageMetric.YoungUniqueCoverage, 5, 20, 5));
        await userDb.SaveChangesAsync();
    }

    /// <summary>Coverage is stored per 1024-deck chunk as basis points (value / 100 = percent).</summary>
    private static UserCoverageChunk Chunk(UserCoverageMetric metric, float deck1, float deck2, float deck3)
    {
        var values = new short[1024];
        values[1] = (short)(deck1 * 100);
        values[2] = (short)(deck2 * 100);
        values[3] = (short)(deck3 * 100);

        return new UserCoverageChunk { UserId = TestUsers.UserA, Metric = (short)metric, ChunkIndex = 0, Values = values };
    }

    private async Task<JsonElement> GetDecksAsync(string query, bool authenticated = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/media-deck/get-media-decks{query}");
        if (authenticated) request.WithUser(TestUsers.UserA);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<int> DeckIds(JsonElement body) =>
        body.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("deckId").GetInt32()).OrderBy(id => id).ToList();

    [Fact]
    public async Task TotalCoverageMin_KeepsDecksWhoseMaturePlusYoungReachesTheFloor()
    {
        await SeedAsync();

        var body = await GetDecksAsync("?totalCoverageMin=70");

        DeckIds(body).Should().Equal(1, 3);
    }

    [Fact]
    public async Task TotalCoverage_IsCappedAtOneHundred()
    {
        await SeedAsync();

        var body = await GetDecksAsync("?totalCoverageMin=99&totalCoverageMax=100");

        DeckIds(body).Should().Equal(3);
    }

    [Fact]
    public async Task UniqueTotalCoverage_FiltersOnUniqueCoverageNotWordCoverage()
    {
        await SeedAsync();

        var body = await GetDecksAsync("?uTotalCoverageMin=80&uTotalCoverageMax=95");

        DeckIds(body).Should().Equal(2);
    }

    [Fact]
    public async Task TotalAndMatureFilters_CombineWithAnd()
    {
        await SeedAsync();

        var body = await GetDecksAsync("?totalCoverageMin=70&coverageMax=50");

        DeckIds(body).Should().Equal(1);
    }

    [Fact]
    public async Task WithoutCoverageParams_ReturnsEveryDeck()
    {
        await SeedAsync();

        var body = await GetDecksAsync("");

        DeckIds(body).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task AnonymousRequest_IgnoresTotalCoverageFilter()
    {
        await SeedAsync();

        var body = await GetDecksAsync("?totalCoverageMin=70", authenticated: false);

        DeckIds(body).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task TotalCoverageFilter_AppliesWhenSortingByTotalCoverage()
    {
        await SeedAsync();

        var body = await GetDecksAsync("?totalCoverageMin=70&sortBy=totalCoverage");

        var decks = body.GetProperty("data").EnumerateArray().ToList();
        decks.Should().HaveCount(2);

        foreach (var deck in decks)
        {
            var total = Math.Min(deck.GetProperty("coverage").GetSingle() + deck.GetProperty("youngCoverage").GetSingle(), 100f);
            total.Should().BeGreaterThanOrEqualTo(70f);
        }
    }
}
