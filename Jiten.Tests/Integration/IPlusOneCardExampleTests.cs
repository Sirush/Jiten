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
        jitenDb.DeckWords.Add(new DeckWord { Deck = deck, WordId = Target, ReadingIndex = 0, Occurrences = 3 });
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

    private static async Task<long> AddSentence(JitenDbContext db, int deckId, string text, SentenceToken[] tokens, bool partial = false,
                                                float difficulty = 0.2f)
    {
        var sentence = new ExampleSentence
        {
            DeckId = deckId, Text = text, Difficulty = difficulty,
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

    /// <summary>One sentence per new title outside the study deck; readable ones get rising difficulties from 0.1.</summary>
    private async Task<(List<long> Readable, List<long> Unknown)> AddOtherTitles(int readable, int unknown)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        async Task<long> AddTitle(string text, SentenceToken[] tokens, float difficulty)
        {
            var deck = new Deck { OriginalTitle = text, MediaType = MediaType.Anime, ReleaseDate = new DateOnly(2020, 1, 1) };
            jitenDb.Decks.Add(deck);
            await jitenDb.SaveChangesAsync();
            return await AddSentence(jitenDb, deck.DeckId, text, tokens, difficulty: difficulty);
        }

        var readableIds = new List<long>();
        for (var i = 0; i < readable; i++)
            readableIds.Add(await AddTitle($"readable {i}", [Token(Target, 0), Token(KnownWord, 2)], 0.1f + i * 0.1f));

        var unknownIds = new List<long>();
        for (var i = 0; i < unknown; i++)
            unknownIds.Add(await AddTitle($"unknown {i}", [Token(Target, 0), Token(UnknownWord, 2)], 0.2f));

        return (readableIds, unknownIds);
    }

    private async Task<ExtraPayload> FirstExtraPage(string sorting = "Random")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/word-example-sentences")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new
            {
                wordId = Target, readingIndex = 0, sorting, take = 3, readableFirst = true,
                minDifficulty = 0, maxDifficulty = 0.5, descending = false,
            });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ExtraPayload>())!;
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
    public async Task ServedCard_WithNoMediaStudyDeck_GetsTheReadableCorpusSentence()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.UserStudyDecks.ExecuteDeleteAsync();
            userDb.UserStudyDecks.Add(new UserStudyDeck
            {
                UserId = TestUsers.UserA, DeckType = StudyDeckType.StaticWordList, Name = "Mining",
            });
            await userDb.SaveChangesAsync();
        }

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

    [Fact]
    public async Task ServedCard_ReadableFirstPage_IsAllReadable_StudyDeckFirst()
    {
        var (readable, _) = await AddOtherTitles(readable: 3, unknown: 4);
        await ServeBatch();

        for (var i = 0; i < 10; i++)
        {
            var page = await FirstExtraPage();
            page.Sentences.Should().HaveCount(3).And.OnlyContain(s => s.IsIPlusOne);
            page.Sentences.Select(s => s.SentenceId).Should().OnlyContain(id => id == _readableId || readable.Contains(id));
            page.Sentences[0].SentenceId.Should().Be(_readableId, "the study deck's readable sentence comes first");
        }
    }

    [Fact]
    public async Task ServedCard_ReadableFirstPage_TopsUpWithRandomSentences()
    {
        var (_, unknown) = await AddOtherTitles(readable: 0, unknown: 3);
        await ServeBatch();

        var page = await FirstExtraPage();
        page.Sentences.Should().HaveCount(3);
        page.Sentences[0].SentenceId.Should().Be(_readableId);
        page.Sentences.Skip(1).Should().OnlyContain(s => unknown.Contains(s.SentenceId) && !s.IsIPlusOne);
    }

    [Fact]
    public async Task CardOutsideTheBatch_ReadableFirstIsIgnored()
    {
        var (_, unknown) = await AddOtherTitles(readable: 3, unknown: 4);

        var pages = new List<SentenceRef>();
        for (var i = 0; i < 10; i++)
            pages.AddRange((await FirstExtraPage()).Sentences);

        pages.Should().OnlyContain(s => !s.IsIPlusOne);
        pages.Should().Contain(s => unknown.Contains(s.SentenceId));
    }

    [Fact]
    public async Task ServedCard_ReadableFirstPage_DifficultySorting_OrdersByDifficultyAndKeepsTheBandCursor()
    {
        await AddOtherTitles(readable: 3, unknown: 4);
        await ServeBatch();

        var page = await FirstExtraPage("EasiestFirst");
        page.Sentences.Should().HaveCount(3).And.OnlyContain(s => s.IsIPlusOne);
        page.Sentences.Select(s => s.Difficulty).Should().BeInAscendingOrder();
        page.SearchedBandMax.Should().Be(0, "a page filled by the readable pick leaves the band walk at its start");
    }

    private class ExtraPayload
    {
        public float SearchedBandMax { get; set; }
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
        public float Difficulty { get; set; }
    }
}
