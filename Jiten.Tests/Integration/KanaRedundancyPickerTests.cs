using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// One word (930) with four forms. Index 0 (一箇所) covers only its kana reading, index 1. Indexes 2 (１カ所)
/// and 3 (１か所) are script variants that cover each other and nothing else, so a card on 0 must not make 2
/// redundant anywhere: not in the picker, not in the counter, not on the word page.
/// </summary>
public class KanaRedundancyPickerTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int Word = 930;
    private const int SetId = 9301;

    private static readonly DateTime Base = new(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc);

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.FsrsCardArchives.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();
        await userDb.UserWordSetStates.ExecuteDeleteAsync();
        await userDb.UserStudyDeckWords.ExecuteDeleteAsync();
        await userDb.UserStudyDecks.ExecuteDeleteAsync();

        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        if (!await jitenDb.WordForms.AnyAsync(wf => wf.WordId == Word))
        {
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = Word, PartsOfSpeech = ["noun"] });
            jitenDb.Definitions.Add(new JmDictDefinition { WordId = Word, SenseIndex = 0, EnglishMeanings = ["one place"], PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.AddRange(
                new JmDictWordForm { WordId = Word, ReadingIndex = 0, Text = "一箇所", RubyText = "一[いっ]箇[か]所[しょ]", FormType = JmDictFormType.KanjiForm },
                new JmDictWordForm { WordId = Word, ReadingIndex = 1, Text = "いっかしょ", RubyText = "いっかしょ", FormType = JmDictFormType.KanaForm },
                new JmDictWordForm { WordId = Word, ReadingIndex = 2, Text = "１カ所", RubyText = "１カ所", FormType = JmDictFormType.KanjiForm },
                new JmDictWordForm { WordId = Word, ReadingIndex = 3, Text = "１か所", RubyText = "１か所", FormType = JmDictFormType.KanjiForm });
            jitenDb.WordSets.Add(new WordSet { SetId = SetId, Slug = "kana-picker-set", Name = "Kana picker set", WordCount = 1 });
            jitenDb.WordSetMembers.Add(new WordSetMember { SetId = SetId, WordId = Word, ReadingIndex = 3, Position = 0 });
            await jitenDb.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<IWordFormSiblingCache>().Reload();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SiblingCache_LinksOnlyTheExpectedForms()
    {
        using var scope = factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IWordFormSiblingCache>();

        cache.GetKanaIndexesForKanji(Word, 0).Should().BeEquivalentTo(new byte[] { 1 });
        cache.GetKanjiIndexesForKana(Word, 2).Should().BeEquivalentTo(new byte[] { 3 });
        cache.GetKanjiIndexesForKana(Word, 2).Should().NotContain(0);
    }

    [Fact]
    public async Task VariantForm_IsDealt_WhenOnlyAnUnrelatedSiblingIsCarded()
    {
        await SeedCard(0, FsrsState.Review);
        await AddDeckWith((Word, 2));

        var dealt = await GetNewCardsFromBatch();

        dealt.Should().Contain((Word, 2));
    }

    [Fact]
    public async Task VariantForm_IsSkipped_WhenItsOwnCoverIsCarded()
    {
        await SeedCard(3, FsrsState.Review);
        await AddDeckWith((Word, 2));

        var dealt = await GetNewCardsFromBatch();

        dealt.Should().NotContain((Word, 2));
    }

    [Fact]
    public async Task VariantForm_IsSkipped_WhenItsOwnCoverIsInAMasteredSet()
    {
        await SetWordSetState(WordSetStateType.Mastered);
        await AddDeckWith((Word, 2));

        var dealt = await GetNewCardsFromBatch();

        dealt.Should().NotContain((Word, 2));
    }

    [Fact]
    public async Task DeckCounter_AgreesWithThePicker()
    {
        await SeedCard(0, FsrsState.Review);
        await SetWordSetState(WordSetStateType.Mastered);
        await AddDeckWith((Word, 1), (Word, 2));

        var decks = await GetDecks();

        // Index 1 is covered by the card on 0, index 2 by the mastered set member 3: nothing left to deal.
        decks.Should().ContainSingle().Which.GetProperty("unseenCount").GetInt32().Should().Be(0);
        (await GetNewCardsFromBatch()).Should().BeEmpty();
    }

    [Fact]
    public async Task WordPage_ShowsTheMasteredSetCoverAsRedundant()
    {
        await SetWordSetState(WordSetStateType.Mastered);

        (await GetKnownState(2)).Should().BeEquivalentTo([KnownState.Mastered, KnownState.Redundant]);
        (await GetKnownState(1)).Should().BeEquivalentTo([KnownState.New]);
    }

    [Fact]
    public async Task ProfileCount_CountsTheMasteredSetCoverAsARedundantForm()
    {
        await SetWordSetState(WordSetStateType.Mastered);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/known-ids/amount").WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var amounts = await response.Content.ReadFromJsonAsync<KnownWordAmountDto>();

        // Set member 3 covers its script variant 2 and nothing else.
        amounts!.WordSetMasteredForm.Should().Be(1);
        amounts.RedundantForms.Should().Be(1);
    }

    [Fact]
    public async Task ProfileCount_LeavesBlacklistedCoversOutOfTheRedundantLine()
    {
        await SetWordSetState(WordSetStateType.Blacklisted);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/known-ids/amount").WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var amounts = await response.Content.ReadFromJsonAsync<KnownWordAmountDto>();

        amounts!.WordSetBlacklistedForm.Should().Be(1);
        amounts.RedundantForms.Should().Be(0);
        (await GetKnownState(2)).Should().BeEquivalentTo([KnownState.Blacklisted, KnownState.Redundant]);
    }

    [Fact]
    public async Task StudyDecksOnly_NeverServesASiblingThatIsNotInTheDeck()
    {
        await SeedCard(3, FsrsState.Review, due: DateTime.UtcNow.AddDays(-1), lastReview: DateTime.UtcNow.AddDays(-10));
        await AddDeckWith((Word, 2));
        await PutSettings(new StudySettingsDto { ReviewFrom = StudyReviewFrom.StudyDecksOnly });

        var cards = await GetBatchCards();

        cards.Should().NotContain(c => c.WordId == Word && c.ReadingIndex == 3);
        cards.Should().NotContain(c => c.IsNew && c.WordId == Word && c.ReadingIndex == 2);
    }

    [Fact]
    public async Task StudyDecksOnly_KeepsTheUsersOwnCardWhenTheDeckFormIsCarded()
    {
        await SeedCard(2, FsrsState.Review, due: DateTime.UtcNow.AddDays(-1), lastReview: DateTime.UtcNow.AddDays(-10));
        await SeedCard(3, FsrsState.Review, due: DateTime.UtcNow.AddDays(-1), lastReview: DateTime.UtcNow.AddDays(-10));
        await AddDeckWith((Word, 2));
        await PutSettings(new StudySettingsDto { ReviewFrom = StudyReviewFrom.StudyDecksOnly });

        var cards = await GetBatchCards();

        cards.Should().Contain(c => !c.IsNew && c.WordId == Word && c.ReadingIndex == 2);
        cards.Should().NotContain(c => c.WordId == Word && c.ReadingIndex == 3);
    }

    private async Task SeedCard(byte readingIndex, FsrsState state, DateTime? due = null, DateTime? lastReview = null)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, Word, readingIndex, state: state,
                                          due: due ?? Base.AddDays(40), lastReview: lastReview ?? Base));
        await userDb.SaveChangesAsync();
    }

    private async Task SetWordSetState(WordSetStateType state)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserWordSetStates.Add(new UserWordSetState { UserId = TestUsers.UserA, SetId = SetId, State = state });
        await userDb.SaveChangesAsync();
    }

    private async Task AddDeckWith(params (int WordId, int ReadingIndex)[] words)
    {
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { deckType = 2, name = "Anime", downloadType = 1, order = 4, minFrequency = 0, maxFrequency = 0, excludeKana = false, excludeMatureMasteredBlacklisted = true, excludeAllTrackedWords = false });
        var createRes = await _client.SendAsync(create);
        createRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var deckId = (await createRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userStudyDeckId").GetInt32();

        var add = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/study-decks/{deckId}/words/batch")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { words = words.Select(w => new { wordId = w.WordId, readingIndex = w.ReadingIndex, occurrences = 1 }).ToArray() });
        (await _client.SendAsync(add)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task PutSettings(StudySettingsDto settings)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(settings);
        (await _client.SendAsync(request)).EnsureSuccessStatusCode();
    }

    private async Task<List<(int WordId, int ReadingIndex, bool IsNew)>> GetBatchCards()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-batch?limit=20").WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        if (!body.TryGetProperty("cards", out var cards) || cards.ValueKind != JsonValueKind.Array)
            return [];

        return cards.EnumerateArray()
            .Select(c => (c.GetProperty("wordId").GetInt32(), c.GetProperty("readingIndex").GetInt32(), c.GetProperty("isNewCard").GetBoolean()))
            .ToList();
    }

    private async Task<List<(int WordId, int ReadingIndex)>> GetNewCardsFromBatch()
        => (await GetBatchCards()).Where(c => c.IsNew).Select(c => (c.WordId, c.ReadingIndex)).ToList();

    private async Task<List<JsonElement>> GetDecks()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-decks").WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        return body.EnumerateArray().ToList();
    }

    private async Task<List<KnownState>> GetKnownState(byte readingIndex)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/vocabulary/{Word}/{readingIndex}/known-state")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<KnownState>>() ?? [];
    }
}
