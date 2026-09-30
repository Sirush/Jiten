using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ReadableSentenceSearchTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int Target = 7101;
    private const int KnownWord = 7102;
    private const int UnknownWord = 7103;
    private const int OtherUnknown = 7104;

    private int _novelDeckId;
    private int _animeDeckId;
    private long _novelReadable;
    private long _novelOneUnknown;
    private long _novelTwoUnknown;
    private long _animeReadable;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<ReadableSentenceListingCache>().Clear();

        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        int[] wordIds = [Target, KnownWord, UnknownWord, OtherUnknown];
        await jitenDb.ExampleSentences.ExecuteDeleteAsync();
        await jitenDb.JMDictWords.Where(w => wordIds.Contains(w.WordId)).ExecuteDeleteAsync();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();

        foreach (var wordId in wordIds)
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = ["noun"] });

        var novel = NewDeck("Novel", MediaType.Novel);
        var anime = NewDeck("Anime", MediaType.Anime);
        jitenDb.Decks.AddRange(novel, anime);
        await jitenDb.SaveChangesAsync();
        _novelDeckId = novel.DeckId;
        _animeDeckId = anime.DeckId;

        _novelReadable = await AddSentence(jitenDb, _novelDeckId, "novel readable", 0.5f, [Token(Target, 0), Token(KnownWord, 2)]);
        _novelOneUnknown = await AddSentence(jitenDb, _novelDeckId, "novel one unknown", 0.2f, [Token(Target, 0), Token(UnknownWord, 2)]);
        _novelTwoUnknown = await AddSentence(jitenDb, _novelDeckId, "novel two unknown", 0.1f,
                                             [Token(Target, 0), Token(UnknownWord, 2), Token(OtherUnknown, 4)]);
        _animeReadable = await AddSentence(jitenDb, _animeDeckId, "anime readable", 0.3f, [Token(Target, 0), Token(KnownWord, 2)]);
        await AddSentence(jitenDb, _novelDeckId, "partial", 0.1f, [Token(Target, 0)], partial: true);

        foreach (var deckId in new[] { _novelDeckId, _animeDeckId })
        {
            await jitenDb.Database.ExecuteSqlRawAsync(
                "INSERT INTO DeckWords (DeckId, WordId, ReadingIndex, Occurrences) VALUES ({0}, {1}, 0, 1)", deckId, Target);
        }

        var now = DateTime.UtcNow;
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, KnownWord, 0, state: FsrsState.Review, stability: 5, difficulty: 5,
                                          due: now.AddDays(5), lastReview: now.AddDays(-1)));

        var userA = await userDb.Users.FirstAsync(u => u.Id == TestUsers.UserA);
        userA.AdminPremiumOverride = true;
        var userB = await userDb.Users.FirstAsync(u => u.Id == TestUsers.UserB);
        userB.AdminPremiumOverride = false;
        await userDb.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        jitenPlus.InvalidateTier(TestUsers.UserA);
        jitenPlus.InvalidateTier(TestUsers.UserB);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Deck NewDeck(string title, MediaType mediaType) => new()
    {
        OriginalTitle = title, MediaType = mediaType, ReleaseDate = new DateOnly(2020, 1, 1),
    };

    private static SentenceToken Token(int wordId, byte position)
        => new(wordId, 0, position, 1, IsTarget: wordId == Target, IsFunctionWord: false);

    private static async Task<long> AddSentence(JitenDbContext db, int deckId, string text, float difficulty, SentenceToken[] tokens,
                                                bool partial = false)
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

    private async Task<HttpResponseMessage> Send(object body, string? userId = TestUsers.UserA)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/vocabulary/{Target}/0/readable-sentences").WithJsonContent(body);
        if (userId != null) request.WithUser(userId);
        return await _client.SendAsync(request);
    }

    private async Task<Payload> Search(object body)
    {
        var response = await Send(body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<Payload>())!;
    }

    [Fact]
    public async Task FreeUser_GetsTheJitenPlusGate()
    {
        var response = await Send(new { unknown = 0 }, TestUsers.UserB);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("feature").GetString().Should().Be("readable-sentences");
    }

    [Fact]
    public async Task Anonymous_IsRejected()
    {
        (await Send(new { unknown = 0 }, userId: null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task IPlusOne_ReturnsOnlyFullyReadableSentences_AndFinishes()
    {
        var result = await Search(new { unknown = 0 });

        result.Sentences.Select(s => s.SentenceId).Should().BeEquivalentTo([_novelReadable, _animeReadable]);
        result.Sentences.Should().OnlyContain(s => s.IsIPlusOne && s.UnknownCount == 0 && s.UnknownSpans!.Count == 0);
        result.Next.Should().BeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Unknown_MatchesExactly(int unknown)
    {
        var result = await Search(new { unknown });

        result.Sentences.Select(s => s.SentenceId).Should().Equal(unknown == 1 ? _novelOneUnknown : _novelTwoUnknown);
        result.Sentences.Should().OnlyContain(s => s.UnknownCount == unknown && !s.IsIPlusOne);
        result.Sentences[0].UnknownSpans!.Select(s => s.Position).Should().Equal(unknown == 1 ? [2] : [2, 4]);
    }

    [Fact]
    public async Task EasiestFirst_OrdersByDifficulty()
    {
        var result = await Search(new { unknown = 0, sort = "EasiestFirst" });

        result.Sentences.Select(s => s.SentenceId).Should().Equal(_animeReadable, _novelReadable);
    }

    [Fact]
    public async Task MediaTypes_FilterBeforeJudging()
    {
        (await Search(new { unknown = 0, mediaTypes = new[] { (int)MediaType.Novel } }))
            .Sentences.Select(s => s.SentenceId).Should().Equal(_novelReadable);
        (await Search(new { unknown = 0, mediaTypes = new[] { (int)MediaType.Novel, (int)MediaType.Anime } }))
            .Sentences.Select(s => s.SentenceId).Should().BeEquivalentTo([_novelReadable, _animeReadable]);
        (await Search(new { unknown = 0, mediaTypes = new[] { (int)MediaType.Manga } }))
            .Sentences.Should().BeEmpty();
    }

    [Fact]
    public async Task Statuses_KeepOnlyMediaMarkedWithThem()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserDeckPreferences.Add(new UserDeckPreference { UserId = TestUsers.UserA, DeckId = _animeDeckId, Status = DeckStatus.Completed });
            userDb.UserDeckPreferences.Add(new UserDeckPreference { UserId = TestUsers.UserA, DeckId = _novelDeckId, Status = DeckStatus.Planning });
            await userDb.SaveChangesAsync();
        }

        (await Search(new { unknown = 0, statuses = new[] { (int)DeckStatus.Completed } }))
            .Sentences.Select(s => s.SentenceId).Should().Equal(_animeReadable);
        (await Search(new { unknown = 0, statuses = new[] { (int)DeckStatus.Planning } }))
            .Sentences.Select(s => s.SentenceId).Should().Equal(_novelReadable);
        (await Search(new { unknown = 0, statuses = new[] { (int)DeckStatus.Ongoing } }))
            .Sentences.Should().BeEmpty();
        (await Search(new { unknown = 0, statuses = new[] { (int)DeckStatus.Planning, (int)DeckStatus.Completed } }))
            .Sentences.Select(s => s.SentenceId).Should().BeEquivalentTo([_animeReadable, _novelReadable]);
    }

    [Fact]
    public async Task Statuses_WithNothingMarked_IsEmpty()
    {
        var result = await Search(new { unknown = 0, statuses = new[] { (int)DeckStatus.Ongoing } });

        result.Sentences.Should().BeEmpty();
        result.Next.Should().BeNull();
    }

    [Fact]
    public async Task Paging_ResumesWithoutRepeats()
    {
        var added = new List<long>();
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            for (var i = 0; i < 25; i++)
                added.Add(await AddSentence(jitenDb, _novelDeckId, $"extra {i}", 0.4f, [Token(Target, 0), Token(KnownWord, 2)]));
        }

        var first = await Search(new { unknown = 0, seed = 7 });
        first.Sentences.Should().HaveCount(20);
        first.Next.Should().NotBeNull();

        var second = await Search(new { unknown = 0, seed = 7, cursor = first.Next });
        second.Next.Should().BeNull();

        var all = first.Sentences.Concat(second.Sentences).Select(s => s.SentenceId).ToList();
        all.Should().OnlyHaveUniqueItems();
        all.Should().BeEquivalentTo(added.Append(_novelReadable).Append(_animeReadable));
    }

    private class Payload
    {
        public List<SentenceRef> Sentences { get; set; } = [];
        public CursorRef? Next { get; set; }
    }

    private class CursorRef
    {
        public int Bucket { get; set; }
        public int Skip { get; set; }
    }

    private class SentenceRef
    {
        public long SentenceId { get; set; }
        public bool IsIPlusOne { get; set; }
        public int? UnknownCount { get; set; }
        public List<SpanRef>? UnknownSpans { get; set; }
    }

    private class SpanRef
    {
        public int Position { get; set; }
        public int Length { get; set; }
    }
}
