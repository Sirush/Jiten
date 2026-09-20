using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class BulkStateAndDeckLearnTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.FsrsCardArchives.ExecuteDeleteAsync();
        await userDb.UserStudyDeckWords.ExecuteDeleteAsync();
        await userDb.UserStudyDecks.ExecuteDeleteAsync();

        // 920 has a kanji form (0) and its kana spelling (1); 921 is a plain noun.
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        if (!await jitenDb.WordForms.AnyAsync(wf => wf.WordId == 920))
        {
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = 920, PartsOfSpeech = ["adj-i"] });
            jitenDb.Definitions.Add(new JmDictDefinition { WordId = 920, SenseIndex = 0, EnglishMeanings = ["cute"], PartsOfSpeech = ["adj-i"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 920, ReadingIndex = 0, Text = "可愛い", RubyText = "可愛[かわい]い", FormType = JmDictFormType.KanjiForm });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 920, ReadingIndex = 1, Text = "かわいい", RubyText = "かわいい", FormType = JmDictFormType.KanaForm });
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = 921, PartsOfSpeech = ["noun"] });
            jitenDb.Definitions.Add(new JmDictDefinition { WordId = 921, SenseIndex = 0, EnglishMeanings = ["book"], PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 921, ReadingIndex = 0, Text = "本", RubyText = "本[ほん]", FormType = JmDictFormType.KanjiForm });
            await jitenDb.SaveChangesAsync();
        }
        scope.ServiceProvider.GetRequiredService<IWordFormSiblingCache>().Reload();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<List<FsrsCard>> LiveCards()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsCards.AsNoTracking().Where(c => c.UserId == TestUsers.UserA).OrderBy(c => c.WordId).ThenBy(c => c.ReadingIndex).ToListAsync();
    }

    [Fact]
    public async Task BulkSetState_DuplicateItems_CreateOneCard()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/set-vocabulary-state-bulk")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new
                                               {
                                                   state = "neverForget-add",
                                                   items = new[]
                                                   {
                                                       new { wordId = 921, readingIndex = 0 },
                                                       new { wordId = 921, readingIndex = 0 },
                                                       new { wordId = 921, readingIndex = 0 },
                                                   }
                                               }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("affectedCount").GetInt32().Should().Be(1);
        var cards = await LiveCards();
        cards.Should().ContainSingle().Which.State.Should().Be(FsrsState.Mastered);
    }

    private async Task<int> CreateStaticDeckWith(params (int WordId, int ReadingIndex)[] words)
    {
        var create = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
                                             .WithUser(TestUsers.UserA)
                                             .WithJsonContent(new
                                             {
                                                 deckType = 2, name = "Kana test", downloadType = 1, order = 4, minFrequency = 0, maxFrequency = 0,
                                                 excludeKana = false, excludeMatureMasteredBlacklisted = false, excludeAllTrackedWords = false
                                             }));
        create.EnsureSuccessStatusCode();
        var deckId = (await create.Content.ReadFromJsonAsync<IdResult>())!.UserStudyDeckId;

        foreach (var (wordId, readingIndex) in words)
        {
            var add = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/words")
                                              .WithUser(TestUsers.UserA)
                                              .WithJsonContent(new { wordId, readingIndex, occurrences = 1 }));
            add.EnsureSuccessStatusCode();
        }
        return deckId;
    }

    private Task<HttpResponseMessage> LearnDeck(int deckId)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/learn")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(new
                             {
                                 downloadType = 1, order = 4, minFrequency = 0, maxFrequency = 0, excludeKana = false,
                                 excludeMatureMasteredBlacklisted = false, excludeAllTrackedWords = false, vocabularyState = "mastered"
                             }));

    [Fact]
    public async Task LearnDeck_WithKanjiAndKanaForms_CreatesOnlyTheKanjiCard()
    {
        var deckId = await CreateStaticDeckWith((920, 0), (920, 1), (921, 0));

        var response = await LearnDeck(deckId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cards = await LiveCards();
        cards.Select(c => (c.WordId, c.ReadingIndex)).Should().BeEquivalentTo([(920, (byte)0), (921, (byte)0)]);
        cards.Should().OnlyContain(c => c.State == FsrsState.Mastered);
    }

    [Fact]
    public async Task LearnDeck_ArchivesALiveKanaCard_TheNewKanjiCardCovers()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, 920, 1, state: FsrsState.Review, stability: 4, difficulty: 5,
                                              due: DateTime.UtcNow.AddDays(1), lastReview: DateTime.UtcNow.AddDays(-3)));
            await userDb.SaveChangesAsync();
        }
        var deckId = await CreateStaticDeckWith((920, 0));

        (await LearnDeck(deckId)).StatusCode.Should().Be(HttpStatusCode.OK);

        var cards = await LiveCards();
        cards.Should().ContainSingle().Which.ReadingIndex.Should().Be(0);
        using var check = factory.Services.CreateScope();
        var archives = await check.ServiceProvider.GetRequiredService<UserDbContext>().FsrsCardArchives
                                  .Where(a => a.UserId == TestUsers.UserA).ToListAsync();
        archives.Should().ContainSingle().Which.ReadingIndex.Should().Be(1);
    }

    private record IdResult(int UserStudyDeckId);
}
