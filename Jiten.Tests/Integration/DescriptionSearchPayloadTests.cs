using System.Net;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class DescriptionSearchPayloadTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SearchByDescription_ReturnsFullListDeckInRankOrder()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.Decks.AddRange(
                new Deck { DeckId = 1, OriginalTitle = "First", MediaType = MediaType.Anime, Difficulty = 2.0f, WordCount = 1234, UniqueKanjiCount = 56 },
                new Deck { DeckId = 2, OriginalTitle = "Second", MediaType = MediaType.Novel, Difficulty = 3.0f, WordCount = 10, UniqueKanjiCount = 2 },
                new Deck { DeckId = 3, OriginalTitle = "Child", MediaType = MediaType.Anime, Difficulty = 2.0f, ParentDeckId = 1 });
            await db.SaveChangesAsync();
        }
        var stub = factory.Services.GetRequiredService<StubDescriptionSearchService>();
        stub.RankedDeckIds.Clear();
        stub.RankedDeckIds.AddRange([2, 1]);

        var response = await _client.GetAsync("/api/media-deck/search-by-description?query=a+cooking+story&limit=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var results = doc.RootElement.GetProperty("results");
        results.GetArrayLength().Should().Be(2);
        results[0].GetProperty("deck").GetProperty("deckId").GetInt32().Should().Be(2);
        results[0].GetProperty("similarityPercent").GetInt32().Should().Be(90);

        // The list views render these; the slim card DTO used by similar-decks does not carry them.
        var first = results[1].GetProperty("deck");
        first.GetProperty("deckId").GetInt32().Should().Be(1);
        first.GetProperty("wordCount").GetInt32().Should().Be(1234);
        first.GetProperty("uniqueKanjiCount").GetInt32().Should().Be(56);
        first.GetProperty("childrenDeckCount").GetInt32().Should().Be(1);
        first.TryGetProperty("relationships", out _).Should().BeTrue();
    }
}
