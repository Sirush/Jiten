using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Api.Services.ReadingCheck;
using Jiten.Api.Services.WordCheck;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class WordCheckTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const int FirstWordId = 9700;
    private const int WordsPerBand = 12;
    private static readonly int[] SeededRanks = [150, 300, 500, 750, 1000, 1500, 2000, 2750, 3500, 5000, 7500, 10000];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client = factory.CreateClient();
    private static readonly HashSet<string> RealTexts = [];

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.CardBatches.ExecuteDeleteAsync();
        await userDb.UserSettings.ExecuteDeleteAsync();
        await userDb.UserWordSetStates.ExecuteDeleteAsync();
        await userDb.UserMediaListEntries.ExecuteDeleteAsync();
        var cache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();
        cache.Remove(OnboardingStore.SetUpCacheKey(TestUsers.UserA));
        cache.Remove(OnboardingStore.SetUpCacheKey(TestUsers.UserB));

        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        if (await jitenDb.WordForms.AnyAsync(wf => wf.WordId == FirstWordId)) return;

        var wordId = FirstWordId;
        var textIndex = 0;
        foreach (var centre in SeededRanks)
        {
            for (var i = 0; i < WordsPerBand; i++)
                AddWord(jitenDb, wordId++, (int)(centre * 0.9) + i * (int)(centre * 0.015) + 1, ref textIndex);
        }

        var grammar = new WordSet { Slug = OnboardingStore.BasicGrammarSlug, Name = "Particles", WordCount = 2 };
        jitenDb.WordSets.Add(grammar);
        await jitenDb.SaveChangesAsync();
        jitenDb.WordSetMembers.AddRange(
            new WordSetMember { SetId = grammar.SetId, WordId = FirstWordId, ReadingIndex = 0 },
            new WordSetMember { SetId = grammar.SetId, WordId = FirstWordId + 1, ReadingIndex = 0 });
        await jitenDb.SaveChangesAsync();
    }

    private static void AddWord(JitenDbContext db, int wordId, int rank, ref int textIndex)
    {
        var text = $"{(char)(0x4E00 + textIndex * 2)}{(char)(0x4E01 + textIndex * 2)}";
        textIndex++;
        RealTexts.Add(text);
        db.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = ["n"] });
        db.Definitions.Add(new JmDictDefinition { WordId = wordId, SenseIndex = 0, EnglishMeanings = ["thing"], PartsOfSpeech = ["n"] });
        db.WordForms.Add(new JmDictWordForm { WordId = wordId, ReadingIndex = 0, Text = text, RubyText = text, FormType = JmDictFormType.KanjiForm });
        db.WordFormFrequencies.Add(new JmDictWordFormFrequency { WordId = wordId, ReadingIndex = 0, FrequencyRank = rank, UsedInMediaAmount = 100 });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<T> Post<T>(string url, object body)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, url).WithUser(TestUsers.UserA).WithJsonContent(body));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private async Task<OnboardingStateDto> GetOnboarding(string? user = null)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/user/onboarding").WithUser(user ?? TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<OnboardingStateDto>(Json))!;
    }

    private async Task<WordCheckStepDto> RunProbe(Func<string, WordCheckAnswer> answer)
    {
        var step = await Post<WordCheckStepDto>("/api/word-check/start", new { level = "Beginner" });
        while (step.Phase == "probe")
        {
            var answers = step.Items.Select(i => new { id = i.Id, answer = answer(i.Text) }).ToList();
            step = await Post<WordCheckStepDto>("/api/word-check/answer", new { sessionId = step.SessionId, answers });
        }

        return step;
    }

    [Fact]
    public async Task HonestAnswers_SeedCommonWordsUnderOneUndoableBatch()
    {
        var step = await RunProbe(text => RealTexts.Contains(text) ? WordCheckAnswer.Know : WordCheckAnswer.No);

        step.Phase.Should().Be("verify");
        var result = await Post<WordCheckResultDto>("/api/word-check/confirm", new { sessionId = step.SessionId, unknownIds = Array.Empty<int>() });

        result.WordsMarked.Should().BeGreaterThan(0);
        result.WordsKnown.Should().BeGreaterThanOrEqualTo(result.WordsMarked);
        result.BatchId.Should().NotBeNull();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var cards = await userDb.FsrsCards.AsNoTracking().Where(c => c.UserId == TestUsers.UserA).ToListAsync();
        cards.Should().NotBeEmpty().And.OnlyContain(c => c.Source == CardSource.PlacementTest && c.SourceBatchId == result.BatchId
                                                         && c.State == FsrsState.Mastered);

        var state = await GetOnboarding();
        state.WordCheckWords.Should().Be(result.WordsKnown);
        state.WordCheckDoneAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ProbeItems_CarryReadingAndMeaningsForRealWordsOnly()
    {
        var step = await Post<WordCheckStepDto>("/api/word-check/start", new { level = "Beginner" });

        step.EstimatedTotal.Should().Be(WordCheckEngine.ProbeQuestions);
        step.Items.Where(i => RealTexts.Contains(i.Text)).Should().HaveCount(WordCheckEngine.WordsPerRound)
            .And.OnlyContain(i => i.Reading != null && i.Meanings != null);
        step.Items.Where(i => !RealTexts.Contains(i.Text)).Should().OnlyContain(i => i.Reading == null && i.Meanings == null);
    }

    [Fact]
    public async Task UnknownWordsOnTheFinalCheck_MarkFewerWords()
    {
        var honest = await RunProbe(text => RealTexts.Contains(text) ? WordCheckAnswer.Know : WordCheckAnswer.No);
        honest.Items.Should().NotBeEmpty();
        var withUnknowns = await Post<WordCheckResultDto>("/api/word-check/confirm",
                                                          new { sessionId = honest.SessionId, unknownIds = honest.Items.Select(i => i.Id) });

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<UserDbContext>().FsrsCards.ExecuteDeleteAsync();

        var again = await RunProbe(text => RealTexts.Contains(text) ? WordCheckAnswer.Know : WordCheckAnswer.No);
        var allKnown = await Post<WordCheckResultDto>("/api/word-check/confirm", new { sessionId = again.SessionId, unknownIds = Array.Empty<int>() });

        withUnknowns.WordsKnown.Should().BeLessThan(allKnown.WordsKnown);
    }

    [Fact]
    public async Task ClaimingEveryWord_FailsWithoutMarkingAnything()
    {
        var step = await RunProbe(_ => WordCheckAnswer.Know);

        step.Phase.Should().Be("failed");
        var result = await Post<WordCheckResultDto>("/api/word-check/confirm", new { sessionId = step.SessionId, unknownIds = Array.Empty<int>() });

        result.WordsMarked.Should().Be(0);
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.FsrsCards.CountAsync(c => c.UserId == TestUsers.UserA)).Should().Be(0);
    }

    [Fact]
    public async Task Onboarding_PromptsNewAccountsMissingTheBasicsUntilALevelIsSet()
    {
        var before = await GetOnboarding();
        before.BasicGrammarMissing.Should().Be(2);
        before.KnownWords.Should().Be(0);
        before.ShouldPrompt.Should().BeTrue();
        before.ShowSetupStrip.Should().BeTrue();

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/user/onboarding")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { level = "Intermediate", opened = true }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await GetOnboarding();
        after.Level.Should().Be(WordCheckLevel.Intermediate);
        after.OpenedAt.Should().NotBeNull();
        after.ShouldPrompt.Should().BeFalse();
    }

    [Fact]
    public async Task KnowingNoCommonWords_StillCountsAsDone()
    {
        var step = await RunProbe(_ => WordCheckAnswer.No);

        step.Phase.Should().Be("verify");
        step.Items.Should().BeEmpty();
        var result = await Post<WordCheckResultDto>("/api/word-check/confirm", new { sessionId = step.SessionId, unknownIds = Array.Empty<int>() });

        result.WordsKnown.Should().Be(0);
        var state = await GetOnboarding();
        state.WordCheckDoneAt.Should().NotBeNull();
        state.WordCheckWords.Should().Be(0);
        state.ShowSetupStrip.Should().BeFalse();
    }

    [Fact]
    public async Task SetUpLater_StopsThePromptButKeepsTheStrip()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/user/onboarding")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { opened = true, dismissed = true }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var state = await GetOnboarding();
        state.ShouldPrompt.Should().BeFalse();
        state.ShowSetupStrip.Should().BeTrue();
    }

    [Fact]
    public async Task UndoingTheWordCheck_AsksForItAgain()
    {
        var step = await RunProbe(text => RealTexts.Contains(text) ? WordCheckAnswer.Know : WordCheckAnswer.No);
        var result = await Post<WordCheckResultDto>("/api/word-check/confirm", new { sessionId = step.SessionId, unknownIds = Array.Empty<int>() });
        (await GetOnboarding()).WordCheckBatchId.Should().Be(result.BatchId);

        var undo = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/user/vocabulary/batches/{result.BatchId}/undo").WithUser(TestUsers.UserA));
        undo.StatusCode.Should().Be(HttpStatusCode.OK);

        var state = await GetOnboarding();
        state.WordCheckDoneAt.Should().BeNull();
        state.WordCheckWords.Should().BeNull();
        state.WordCheckBatchId.Should().BeNull();
    }

    [Fact]
    public async Task ClearingAllKnownWords_AsksForTheWordCheckAgain()
    {
        var level = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/user/onboarding")
                                            .WithUser(TestUsers.UserA)
                                            .WithJsonContent(new { level = "Intermediate", opened = true, dismissed = true }));
        level.StatusCode.Should().Be(HttpStatusCode.OK);
        var step = await RunProbe(text => RealTexts.Contains(text) ? WordCheckAnswer.Know : WordCheckAnswer.No);
        await Post<WordCheckResultDto>("/api/word-check/confirm", new { sessionId = step.SessionId, unknownIds = Array.Empty<int>() });
        (await GetOnboarding()).ShowSetupStrip.Should().BeFalse();

        var clear = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/user/vocabulary/known-ids/clear").WithUser(TestUsers.UserA));
        clear.StatusCode.Should().Be(HttpStatusCode.OK);

        var state = await GetOnboarding();
        state.WordCheckDoneAt.Should().BeNull();
        state.WordCheckWords.Should().BeNull();
        state.WordCheckBatchId.Should().BeNull();
        state.ReadingCheck.Should().BeNull();
        state.Level.Should().Be(WordCheckLevel.Intermediate);
        state.ShowSetupStrip.Should().BeTrue();
        state.ShouldPrompt.Should().BeFalse();
    }

    [Fact]
    public async Task OldAccounts_ArePromptedWhileNotSetUp()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.Users.Where(u => u.Id == TestUsers.UserA)
                        .ExecuteUpdateAsync(s => s.SetProperty(u => u.CreatedAt, DateTime.UtcNow.AddYears(-2)));
        }

        try
        {
            var state = await GetOnboarding();
            state.ShouldPrompt.Should().BeTrue();
            state.ShowSetupStrip.Should().BeTrue();
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<UserDbContext>().Users.Where(u => u.Id == TestUsers.UserA)
                       .ExecuteUpdateAsync(s => s.SetProperty(u => u.CreatedAt, DateTime.UtcNow));
        }
    }

    [Fact]
    public async Task KnowingTheGrammarWords_IsNotEnoughBelowTheWordThreshold()
    {
        await AddMasteredCards(TestUsers.UserB, [FirstWordId, FirstWordId + 1, ..Enumerable.Range(200_000, OnboardingStore.SetUpKnownWords - 3)]);

        var state = await GetOnboarding(TestUsers.UserB);

        state.BasicGrammarMissing.Should().Be(0);
        state.KnownWords.Should().Be(OnboardingStore.SetUpKnownWords - 1);
        state.ShouldPrompt.Should().BeTrue();
    }

    [Fact]
    public async Task GrammarWordsPlusTheWordThreshold_StopsThePrompt()
    {
        await AddMasteredCards(TestUsers.UserB, [FirstWordId, FirstWordId + 1, ..Enumerable.Range(200_000, OnboardingStore.SetUpKnownWords - 2)]);

        var state = await GetOnboarding(TestUsers.UserB);

        state.ShouldPrompt.Should().BeFalse();
        state.ShowSetupStrip.Should().BeFalse();
    }

    [Fact]
    public async Task ManyWordsWithoutTheGrammarWords_StillPrompts()
    {
        await AddMasteredCards(TestUsers.UserA, Enumerable.Range(200_000, OnboardingStore.SetUpKnownWords + 500).ToList());

        var state = await GetOnboarding();

        state.BasicGrammarMissing.Should().Be(2);
        state.ShouldPrompt.Should().BeTrue();
    }

    private async Task AddMasteredCards(string user, IReadOnlyList<int> wordIds)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var now = DateTime.UtcNow;
        userDb.FsrsCards.AddRange(wordIds.Select(id => new FsrsCard(user, id, 0, due: now, lastReview: now, state: FsrsState.Mastered)));
        await userDb.SaveChangesAsync();
    }

    [Fact]
    public async Task Basics_CountOnlyLearnedForms()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var now = DateTime.UtcNow;
            userDb.FsrsCards.AddRange(
                new FsrsCard(TestUsers.UserA, FirstWordId, 0, due: now, state: FsrsState.New),
                new FsrsCard(TestUsers.UserA, FirstWordId + 1, 0, due: now, lastReview: now, state: FsrsState.Mastered),
                new FsrsCard(TestUsers.UserA, FirstWordId + 2, 0, due: now, lastReview: now, state: FsrsState.Blacklisted));
            await userDb.SaveChangesAsync();
        }

        var state = await GetOnboarding();
        state.BasicGrammarMissing.Should().Be(1);
        state.KnownWords.Should().Be(1);
    }

    [Fact]
    public async Task ReadingCheck_ListsFinishedVolumesAndRefusesTooFewUnits()
    {
        int parentId;
        List<int> volumeIds;
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var parent = new Deck { OriginalTitle = "Series", MediaType = MediaType.Manga, CharacterCount = 4000 };
            jitenDb.Decks.Add(parent);
            await jitenDb.SaveChangesAsync();
            var volumes = Enumerable.Range(1, 4)
                                    .Select(i => new Deck { OriginalTitle = $"Volume {i}", MediaType = MediaType.Manga, ParentDeckId = parent.DeckId, DeckOrder = i })
                                    .ToList();
            jitenDb.Decks.AddRange(volumes);
            await jitenDb.SaveChangesAsync();
            parentId = parent.DeckId;
            volumeIds = volumes.Select(v => v.DeckId).ToList();

            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserMediaListEntries.AddRange(
                new UserMediaListEntry { UserId = TestUsers.UserA, DeckId = volumeIds[0], State = MediaListEntryState.Completed },
                new UserMediaListEntry { UserId = TestUsers.UserA, DeckId = volumeIds[1], State = MediaListEntryState.Completed },
                new UserMediaListEntry { UserId = TestUsers.UserA, DeckId = volumeIds[2], State = MediaListEntryState.InProgress });
            await userDb.SaveChangesAsync();
        }

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/reading-check/works").WithUser(TestUsers.UserA));
        var works = (await response.Content.ReadFromJsonAsync<List<ReadingWorkDto>>(Json))!;

        var row = works.Should().ContainSingle().Subject;
        row.Key.Should().Be($"deck-{parentId}");
        row.Units.Select(u => u.Ticked).Should().Equal(true, true, false, false);
        row.Units[2].InProgress.Should().BeTrue();

        var count = await Post<ReadingCountDto>("/api/reading-check/count", new { deckIds = new[] { parentId } });
        count.Units.Should().Be(4);

        var start = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/reading-check/start")
                                            .WithUser(TestUsers.UserA)
                                            .WithJsonContent(new { deckIds = volumeIds.Take(2) }));
        start.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnsweringAnotherUsersOrAMissingSession_IsGone()
    {
        var step = await Post<WordCheckStepDto>("/api/word-check/start", new { level = "Beginner" });
        var answers = step.Items.Select(i => new { id = i.Id, answer = WordCheckAnswer.No }).ToList();

        var asOther = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/word-check/answer")
                                              .WithUser(TestUsers.UserB)
                                              .WithJsonContent(new { sessionId = step.SessionId, answers }));
        var missing = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/word-check/answer")
                                              .WithUser(TestUsers.UserA)
                                              .WithJsonContent(new { sessionId = "none", answers }));

        asOther.StatusCode.Should().Be(HttpStatusCode.Gone);
        missing.StatusCode.Should().Be(HttpStatusCode.Gone);
    }
}
