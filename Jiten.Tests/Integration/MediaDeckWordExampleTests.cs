using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class MediaDeckWordExampleTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int Target = 1000001, Other = 1000002;

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static ExampleSentence Sentence(int deckId, string text, params int[] wordIds)
    {
        var tokens = wordIds.Select((w, i) => new SentenceToken(w, 0, (byte)(i * 2), 2, IsTarget: i == 0, IsFunctionWord: false)).ToArray();
        return new ExampleSentence
        {
            DeckId = deckId, Text = text, Difficulty = 1,
            Tokens = ExampleSentenceTokens.Encode(tokens),
            WordKeys = ExampleSentenceTokens.WordKeys(tokens, 0)
        };
    }

    private async Task<(int Series, int Novel, int Bare)> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var series = new Deck { OriginalTitle = "Series", MediaType = MediaType.Anime };
        var novel = new Deck { OriginalTitle = "Novel", MediaType = MediaType.Novel };
        var bare = new Deck { OriginalTitle = "Bare", MediaType = MediaType.Novel };
        db.Decks.AddRange(series, novel, bare);
        await db.SaveChangesAsync();

        var episode1 = new Deck { OriginalTitle = "Episode 1", MediaType = MediaType.Anime, ParentDeckId = series.DeckId, DeckOrder = 1 };
        var episode2 = new Deck { OriginalTitle = "Episode 2", MediaType = MediaType.Anime, ParentDeckId = series.DeckId, DeckOrder = 2 };
        db.Decks.AddRange(episode1, episode2);
        foreach (var deck in new[] { series, novel, bare })
            db.DeckWords.Add(new DeckWord { DeckId = deck.DeckId, WordId = Target, ReadingIndex = 0, Occurrences = 1 });
        await db.SaveChangesAsync();

        db.ExampleSentences.Add(Sentence(episode2.DeckId, "二話目", Target, Other));
        await db.SaveChangesAsync();
        db.ExampleSentences.AddRange(
            Sentence(episode1.DeckId, "一話目", Target),
            Sentence(novel.DeckId, "無関係", Other),
            Sentence(novel.DeckId, "小説", Other, Target),
            Sentence(bare.DeckId, "なし", Other));
        await db.SaveChangesAsync();

        return (series.DeckId, novel.DeckId, bare.DeckId);
    }

    [Fact]
    public async Task WordFilter_AttachesTheEarliestSentenceAcrossEachTitlesSubdecks()
    {
        var (series, novel, bare) = await SeedAsync();

        var response = await _client.GetAsync($"/api/media-deck/get-media-decks?wordId={Target}&readingIndex=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var decks = body.GetProperty("data").EnumerateArray().ToDictionary(d => d.GetProperty("deckId").GetInt32());
        decks.Keys.Should().BeEquivalentTo([series, novel, bare]);

        decks[series].GetProperty("exampleSentence").GetProperty("text").GetString().Should().Be("二話目");
        decks[novel].GetProperty("exampleSentence").GetProperty("text").GetString().Should().Be("小説");
        decks[novel].GetProperty("exampleSentence").GetProperty("wordPosition").GetInt32().Should().Be(2);
        decks[bare].GetProperty("exampleSentence").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
