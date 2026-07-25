using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class SubdeckFilterTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>DifficultyOverride must be -1 to opt out of the override, matching what ParseJob writes.</summary>
    private static Deck Child(int parentId, string original, int order, float difficulty) => new()
    {
        OriginalTitle = original, MediaType = MediaType.Anime, ParentDeckId = parentId, DeckOrder = order,
        Difficulty = difficulty, DifficultyOverride = -1,
    };

    private async Task<int> SeedSeries()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var parent = new Deck { OriginalTitle = "Test Series", MediaType = MediaType.Anime, DifficultyOverride = -1 };
        db.Decks.Add(parent);
        await db.SaveChangesAsync();

        var first = Child(parent.DeckId, "第1話", 1, 4.0f);
        first.EnglishTitle = "Episode 1";
        var second = Child(parent.DeckId, "第2話", 2, 2.0f);
        second.EnglishTitle = "Episode 2 Special";
        var third = Child(parent.DeckId, "総集編", 3, 6.0f);
        third.RomajiTitle = "Soushuuhen";

        db.Decks.AddRange(first, second, third);
        await db.SaveChangesAsync();

        return parent.DeckId;
    }

    private async Task<JsonElement> GetDetail(int deckId, string query = "")
    {
        var response = await _client.GetAsync($"/api/media-deck/{deckId}/detail{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<string> SubdeckTitles(JsonElement body) =>
        body.GetProperty("data").GetProperty("subDecks")
            .EnumerateArray()
            .Select(d => d.GetProperty("originalTitle").GetString()!)
            .ToList();

    [Fact]
    public async Task Detail_WithoutParams_ReturnsAllChildrenInDeckOrder()
    {
        var parentId = await SeedSeries();

        var body = await GetDetail(parentId);

        SubdeckTitles(body).Should().Equal("第1話", "第2話", "総集編");
        body.GetProperty("totalItems").GetInt32().Should().Be(3);
        body.GetProperty("pageSize").GetInt32().Should().Be(25);
    }

    [Fact]
    public async Task Detail_SubdeckFilter_MatchesOriginalTitleSubstring()
    {
        var parentId = await SeedSeries();

        var body = await GetDetail(parentId, "?subdeckFilter=第2");

        SubdeckTitles(body).Should().Equal("第2話");
        body.GetProperty("totalItems").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Detail_SubdeckFilter_MatchesEnglishTitleCaseInsensitively()
    {
        var parentId = await SeedSeries();

        var body = await GetDetail(parentId, "?subdeckFilter=SPECIAL");

        SubdeckTitles(body).Should().Equal("第2話");
    }

    [Fact]
    public async Task Detail_SubdeckFilter_MatchesRomajiTitle()
    {
        var parentId = await SeedSeries();

        var body = await GetDetail(parentId, "?subdeckFilter=soushuu");

        SubdeckTitles(body).Should().Equal("総集編");
    }

    [Fact]
    public async Task Detail_SubdeckFilter_TreatsWildcardsLiterally()
    {
        var parentId = await SeedSeries();

        var body = await GetDetail(parentId, "?subdeckFilter=%25");

        SubdeckTitles(body).Should().BeEmpty();
        body.GetProperty("totalItems").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Detail_SubdeckFilter_NoMatchesStillReturnsDeck()
    {
        var parentId = await SeedSeries();

        var body = await GetDetail(parentId, "?subdeckFilter=nothingmatchesthis");

        body.GetProperty("data").GetProperty("mainDeck").GetProperty("originalTitle").GetString().Should().Be("Test Series");
        SubdeckTitles(body).Should().BeEmpty();
    }

    [Fact]
    public async Task Detail_SubdeckSortDifficulty_OrdersEasiestFirst()
    {
        var parentId = await SeedSeries();

        var body = await GetDetail(parentId, "?subdeckSort=Difficulty");

        SubdeckTitles(body).Should().Equal("第2話", "第1話", "総集編");
    }

    [Fact]
    public async Task Detail_SubdeckSortDifficultyDescending_OrdersHardestFirst()
    {
        var parentId = await SeedSeries();

        var body = await GetDetail(parentId, "?subdeckSort=Difficulty&subdeckSortOrder=Descending");

        SubdeckTitles(body).Should().Equal("総集編", "第1話", "第2話");
    }

    [Fact]
    public async Task Detail_SubdeckSortOrderDescending_ReversesDeckOrder()
    {
        var parentId = await SeedSeries();

        var body = await GetDetail(parentId, "?subdeckSortOrder=Descending");

        SubdeckTitles(body).Should().Equal("総集編", "第2話", "第1話");
    }

    [Fact]
    public async Task Detail_SubdeckFilterWithSort_AppliesBoth()
    {
        var parentId = await SeedSeries();

        var body = await GetDetail(parentId, "?subdeckFilter=episode&subdeckSort=Difficulty");

        SubdeckTitles(body).Should().Equal("第2話", "第1話");
        body.GetProperty("totalItems").GetInt32().Should().Be(2);
    }
}
