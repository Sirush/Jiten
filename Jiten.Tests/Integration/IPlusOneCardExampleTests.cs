using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class IPlusOneCardExampleTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int Target = 7001;
    private const int KnownWord = 7002;
    private const int UnknownWord = 7003;
    private const int Particle = 7004;
    private const int Name = 5_000_123;

    private long _readableId;
    private long _unknownId;
    private long _partialId;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        ((InMemoryStudySessionService)factory.Services.GetRequiredService<IStudySessionService>()).ClearServedCards();

        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        int[] wordIds = [Target, KnownWord, UnknownWord, Particle, Name];
        await jitenDb.ExampleSentences.ExecuteDeleteAsync();
        await jitenDb.JMDictWords.Where(w => wordIds.Contains(w.WordId)).ExecuteDeleteAsync();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();
        await userDb.UserExampleSentences.ExecuteDeleteAsync();

        foreach (var wordId in wordIds)
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = ["noun"] });

        var deck = new Deck { OriginalTitle = "Study", MediaType = MediaType.Novel, ReleaseDate = new DateOnly(2020, 1, 1) };
        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();

        _readableId = await AddSentence(jitenDb, deck.DeckId, "readable",
                                        [Token(Target, 0), Token(KnownWord, 2), Token(Particle, 4, functionWord: true), Token(Name, 6)]);
        _unknownId = await AddSentence(jitenDb, deck.DeckId, "unknown", [Token(Target, 0), Token(UnknownWord, 2)]);
        _partialId = await AddSentence(jitenDb, deck.DeckId, "partial", [Token(Target, 0)], partial: true);

        userDb.UserStudyDecks.Add(new UserStudyDeck
        {
            UserId = TestUsers.UserA, DeckType = StudyDeckType.MediaDeck, Name = "Study", DeckId = deck.DeckId,
        });

        var now = DateTime.UtcNow;
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, Target, 0, state: FsrsState.Review, stability: 5, difficulty: 5,
                                          due: now.AddDays(-1), lastReview: now.AddDays(-6)));
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, KnownWord, 0, state: FsrsState.Review, stability: 5, difficulty: 5,
                                          due: now.AddDays(5), lastReview: now.AddDays(-1)));
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static SentenceToken Token(int wordId, byte position, bool functionWord = false)
        => new(wordId, 0, position, 1, IsTarget: wordId == Target, IsFunctionWord: functionWord);

    private static async Task<long> AddSentence(JitenDbContext db, int deckId, string text, SentenceToken[] tokens, bool partial = false)
    {
        var sentence = new ExampleSentence
        {
            DeckId = deckId, Text = text, Difficulty = 0.2f,
            Tokens = ExampleSentenceTokens.Encode(tokens, partial),
            WordKeys = ExampleSentenceTokens.WordKeys(tokens, Random.Shared.Next(ExampleSentenceTokens.FineBucketCount)),
        };
        db.ExampleSentences.Add(sentence);
        await db.SaveChangesAsync();
        return sentence.SentenceId;
    }

    private async Task ServeBatch()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-batch?limit=10")
                                                   .WithUser(TestUsers.UserA));
        response.EnsureSuccessStatusCode();
        var batch = await response.Content.ReadFromJsonAsync<BatchPayload>();
        batch!.Cards.Should().Contain(c => c.WordId == Target);
    }

    private async Task<SentenceRef> CardExample()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/card-examples")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { pairs = new[] { new { wordId = Target, readingIndex = 0 } } });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<CardExamplesPayload>();
        return payload!.Examples[$"{Target}-0"];
    }

    private async Task<HashSet<long>> PickedSentences(int calls)
    {
        var seen = new HashSet<long>();
        for (var i = 0; i < calls; i++)
            seen.Add((await CardExample()).SentenceId);
        return seen;
    }

    private async Task<List<SentenceRef>> ExtraSentences(int calls)
    {
        var all = new List<SentenceRef>();
        for (var i = 0; i < calls; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/word-example-sentences")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(new { wordId = Target, readingIndex = 0, sorting = "Random", take = 3 });
            var response = await _client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            all.AddRange((await response.Content.ReadFromJsonAsync<ExtraPayload>())!.Sentences);
        }

        return all;
    }

    private async Task SetExampleSentenceSource(string source)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserFsrsSettings.Add(new UserFsrsSettings
        {
            UserId = TestUsers.UserA, SettingsJson = $"{{\"exampleSentenceSource\":\"{source}\"}}",
        });
        await userDb.SaveChangesAsync();
    }

    [Fact]
    public async Task ServedCard_AlwaysGetsTheSentenceWithEveryOtherWordKnown()
    {
        await ServeBatch();

        (await PickedSentences(20)).Should().Equal(_readableId);
    }

    [Fact]
    public async Task ServedCard_RandomSource_AlsoGetsTheReadableSentence()
    {
        await SetExampleSentenceSource("Random");
        await ServeBatch();

        (await PickedSentences(20)).Should().Equal(_readableId);
    }

    [Fact]
    public async Task CardOutsideTheBatch_KeepsTheRandomPick()
    {
        (await PickedSentences(30)).Should().Contain(_unknownId);
    }

    [Fact]
    public async Task PartialRow_NeverCountsAsReadable()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            await jitenDb.ExampleSentences.Where(s => s.SentenceId == _readableId).ExecuteDeleteAsync();
        }

        await ServeBatch();

        (await PickedSentences(30)).Should().BeEquivalentTo([_unknownId, _partialId],
                                                            "with no i+1 sentence the pick stays random over the pool");
    }

    [Fact]
    public async Task ServedCard_WithTheOtherWordUnknown_FallsBackToTheRandomPick()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.FsrsCards.Where(c => c.WordId == KnownWord).ExecuteDeleteAsync();
        }

        await ServeBatch();

        (await PickedSentences(30)).Should().Contain(_readableId).And.Contain(_unknownId);
    }

    [Fact]
    public async Task ServedCard_ReadablePick_IsFlagged()
    {
        await ServeBatch();

        var example = await CardExample();
        example.SentenceId.Should().Be(_readableId);
        example.IsIPlusOne.Should().BeTrue();
    }

    [Fact]
    public async Task CardOutsideTheBatch_IsNeverFlagged()
    {
        for (var i = 0; i < 20; i++)
            (await CardExample()).IsIPlusOne.Should().BeFalse();
    }

    [Fact]
    public async Task ServedCard_ExtraSentences_FlagOnlyTheReadableOne()
    {
        await ServeBatch();

        var extras = await ExtraSentences(20);
        extras.Should().Contain(s => s.SentenceId == _readableId);
        extras.Should().OnlyContain(s => s.IsIPlusOne == (s.SentenceId == _readableId));
    }

    [Fact]
    public async Task CardOutsideTheBatch_ExtraSentencesAreNeverFlagged()
    {
        var extras = await ExtraSentences(20);
        extras.Should().Contain(s => s.SentenceId == _readableId);
        extras.Should().OnlyContain(s => !s.IsIPlusOne);
    }

    private class ExtraPayload
    {
        public List<SentenceRef> Sentences { get; set; } = [];
    }

    private class BatchPayload
    {
        public List<CardRef> Cards { get; set; } = [];
    }

    private class CardRef
    {
        public int WordId { get; set; }
    }

    private class CardExamplesPayload
    {
        public Dictionary<string, SentenceRef> Examples { get; set; } = new();
    }

    private class SentenceRef
    {
        public long SentenceId { get; set; }
        public bool IsIPlusOne { get; set; }
    }
}
