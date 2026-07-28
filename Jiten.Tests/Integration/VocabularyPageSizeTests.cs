using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class VocabularyPageSizeTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedDeck()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var deck = new Deck { OriginalTitle = "Page Size Deck", MediaType = MediaType.Novel, DifficultyOverride = -1 };
        db.Decks.Add(deck);
        await db.SaveChangesAsync();

        return deck.DeckId;
    }

    private async Task<int> GetEchoedPageSize(int deckId, string query)
    {
        var response = await _client.GetAsync($"/api/media-deck/{deckId}/vocabulary{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("pageSize").GetInt32();
    }

    [Fact]
    public async Task DeckVocabulary_WithoutLimit_KeepsHistoricalPageSize()
    {
        var deckId = await SeedDeck();

        (await GetEchoedPageSize(deckId, "")).Should().Be(100);
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(200, 200)]
    public async Task DeckVocabulary_WithSupportedLimit_UsesIt(int limit, int expected)
    {
        var deckId = await SeedDeck();

        (await GetEchoedPageSize(deckId, $"?limit={limit}")).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-25, 1)]
    [InlineData(5000, 200)]
    public async Task DeckVocabulary_WithOutOfRangeLimit_IsClamped(int limit, int expected)
    {
        var deckId = await SeedDeck();

        (await GetEchoedPageSize(deckId, $"?limit={limit}")).Should().Be(expected);
    }
}
