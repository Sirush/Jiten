using System.Net;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class DeckRelationshipPayloadTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Detail_RelationshipTarget_CarriesTitlesOnly()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.Decks.AddRange(
                new Deck { DeckId = 1, OriginalTitle = "Source", MediaType = MediaType.Anime, Difficulty = 2.0f },
                new Deck
                {
                    DeckId = 2, OriginalTitle = "Target", RomajiTitle = "Taagetto", EnglishTitle = "The Target",
                    MediaType = MediaType.Anime, Difficulty = 2.0f, Description = "A long description that must not ship"
                });
            await db.SaveChangesAsync();
            db.DeckRelationships.Add(new DeckRelationship
            {
                SourceDeckId = 1, TargetDeckId = 2, RelationshipType = DeckRelationshipType.Sequel
            });
            await db.SaveChangesAsync();
        }

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/media-deck/1/detail");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var relationships = doc.RootElement.GetProperty("data").GetProperty("mainDeck").GetProperty("relationships");
        relationships.GetArrayLength().Should().Be(1);
        var target = relationships[0].GetProperty("targetDeck");

        target.GetProperty("deckId").GetInt32().Should().Be(2);
        target.GetProperty("originalTitle").GetString().Should().Be("Target");
        target.GetProperty("romajiTitle").GetString().Should().Be("Taagetto");
        target.GetProperty("englishTitle").GetString().Should().Be("The Target");
        target.TryGetProperty("description", out _).Should().BeFalse();
        target.TryGetProperty("tags", out _).Should().BeFalse();
        target.TryGetProperty("relationships", out _).Should().BeFalse();
    }
}
