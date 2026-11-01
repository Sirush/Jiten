using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class TargetCoverageTagFilterTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int DeckId = 1;
    private const string Verbs = """["v1","v5k"]""";
    private const string Nouns = """["n"]""";

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        await Seed();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>100 tokens: particle 1 (50, mastered), verb 2 (20), noun 3 (15), verb 4 (10), noun 5 (5).</summary>
    private async Task Seed()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await db.DeckWords.ExecuteDeleteAsync();
        await db.Definitions.ExecuteDeleteAsync();
        await db.WordForms.ExecuteDeleteAsync();
        await db.JMDictWords.ExecuteDeleteAsync();

        db.Decks.Add(new Deck
        {
            DeckId = DeckId, OriginalTitle = "Book", MediaType = MediaType.Novel, CreationDate = DateTime.UtcNow,
            CharacterCount = 1000, WordCount = 100, UniqueWordCount = 5
        });
        await db.SaveChangesAsync();

        (int WordId, string Pos, int Occurrences)[] words = [(1, "prt", 50), (2, "v1", 20), (3, "n", 15), (4, "v5k", 10), (5, "n", 5)];
        foreach (var (wordId, pos, occurrences) in words)
        {
            db.DeckWords.Add(new DeckWord { DeckId = DeckId, WordId = wordId, ReadingIndex = 0, Occurrences = occurrences });
            db.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = [pos] });
            db.Definitions.Add(new JmDictDefinition { WordId = wordId, SenseIndex = 0, EnglishMeanings = [$"meaning{wordId}"], PartsOfSpeech = [pos] });
            db.WordForms.Add(new JmDictWordForm { WordId = wordId, ReadingIndex = 0, Text = $"語{wordId}", RubyText = $"語{wordId}", FormType = JmDictFormType.KanjiForm });
            await db.SaveChangesAsync();
        }

        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, 1, 0, state: FsrsState.Mastered));
        await userDb.SaveChangesAsync();
    }

    private async Task<int> PreviewTotalAsync(float target, string posFilter, bool startFromKnown)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks/preview-count")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new
                                               {
                                                   deckId = DeckId, downloadType = 5, order = 3, targetPercentage = target, posFilter,
                                                   startFromKnown
                                               }));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("total").GetInt32();
    }

    /// <summary>Unfiltered 85% needs words 2 and 3; only 2 is a verb.</summary>
    [Fact]
    public async Task TagFilter_NarrowsTheWholeDeckCoverageSet()
    {
        (await PreviewTotalAsync(85, Verbs, startFromKnown: false)).Should().Be(1);
    }

    /// <summary>The mastered particle already covers 50%, so noun 3 alone reaches 65%.</summary>
    [Fact]
    public async Task StartFromKnown_CountsKnownWordsOutsideTheFilter()
    {
        (await PreviewTotalAsync(60, Nouns, startFromKnown: true)).Should().Be(1);
    }

    [Fact]
    public async Task StartFromKnown_PicksNothingOnceTheTargetIsMet()
    {
        (await PreviewTotalAsync(50, Nouns, startFromKnown: true)).Should().Be(0);
    }

    [Fact]
    public async Task UnreachableTarget_TakesEveryMatchingWord()
    {
        (await PreviewTotalAsync(95, Nouns, startFromKnown: true)).Should().Be(2);
    }
}
