using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// 強い (920) → 強さ (921) as a さ-nominalisation, and 強い → 強がる (922) as a がる verb, both with the
/// builder's form closure: kanji base index 0, kana base index 1.
/// </summary>
public class DerivationRedundancyTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int Tsuyoi = 920;
    private const int Tsuyosa = 921;
    private const int Tsuyogaru = 922;
    private const string SaKey = "sa_i_adj";
    private const string GaruKey = "garu_both";

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
        await userDb.UserMetadatas.ExecuteDeleteAsync();
        await userDb.UserStudyDeckWords.ExecuteDeleteAsync();
        await userDb.UserStudyDecks.ExecuteDeleteAsync();

        // The settings row is dropped behind the API's back, so the short-TTL category cache must go with it.
        DerivationSettingsHelper.Invalidate(scope.ServiceProvider.GetRequiredService<IMemoryCache>(), TestUsers.UserA);

        await SeedDictionary();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedDictionary()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        if (!await jitenDb.WordForms.AnyAsync(wf => wf.WordId == Tsuyoi))
        {
            AddWord(jitenDb, Tsuyoi, "強い", "つよい");
            AddWord(jitenDb, Tsuyosa, "強さ", "つよさ");
            AddWord(jitenDb, Tsuyogaru, "強がる", "つよがる");
            await jitenDb.SaveChangesAsync();
        }

        await jitenDb.WordDerivations.ExecuteDeleteAsync();
        jitenDb.WordDerivations.AddRange([
            ..FormClosure(Tsuyoi, Tsuyosa, DerivationCategory.SaNominal),
            ..FormClosure(Tsuyoi, Tsuyogaru, DerivationCategory.Garu)
        ]);
        await jitenDb.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IDerivationLinkCache>().Reload();
    }

    private static void AddWord(JitenDbContext db, int wordId, string kanji, string kana)
    {
        db.JMDictWords.Add(new JmDictWord { WordId = wordId, PartsOfSpeech = ["adj-i"] });
        db.WordForms.Add(new JmDictWordForm
        {
            WordId = wordId, ReadingIndex = 0, Text = kanji, RubyText = kanji, FormType = JmDictFormType.KanjiForm
        });
        db.WordForms.Add(new JmDictWordForm
        {
            WordId = wordId, ReadingIndex = 1, Text = kana, RubyText = kana, FormType = JmDictFormType.KanaForm
        });
    }

    private static JmDictWordDerivation[] FormClosure(int baseWordId, int derivedWordId, DerivationCategory category)
        =>
        [
            Row(baseWordId, 0, derivedWordId, 0, category),
            Row(baseWordId, 0, derivedWordId, 1, category),
            Row(baseWordId, 1, derivedWordId, 1, category)
        ];

    private static JmDictWordDerivation Row(int baseWordId, byte baseIndex, int derivedWordId, byte derivedIndex,
                                            DerivationCategory category)
        => new()
        {
            BaseWordId = baseWordId, BaseReadingIndex = baseIndex,
            DerivedWordId = derivedWordId, DerivedReadingIndex = derivedIndex,
            Category = category, Direction = DerivationDirection.Bidirectional,
            Source = DerivationSource.RuleGenerated
        };

    private async Task SeedCard(int wordId, byte readingIndex, FsrsState state, DateTime? due = null,
                                DateTime? lastReview = null)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, readingIndex, state: state,
                                          due: due ?? Base.AddDays(40), lastReview: lastReview ?? Base));
        await userDb.SaveChangesAsync();
    }

    private async Task SetCategories(params string[] categories)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new StudySettingsDto { DerivationalRedundancyCategories = [..categories] });
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<List<KnownState>> GetKnownState(int wordId, byte readingIndex)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/vocabulary/{wordId}/{readingIndex}/known-state")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<KnownState>>() ?? [];
    }

    [Fact]
    public async Task DerivedForm_StaysNew_WhenNoCategoryIsEnabled()
    {
        await SetCategories();
        await SeedCard(Tsuyoi, 0, FsrsState.Mastered);

        (await GetKnownState(Tsuyosa, 0)).Should().BeEquivalentTo([KnownState.New]);
    }

    [Fact]
    public async Task MasteredBase_MakesTheDerivedFormRedundantWithTheSameTier()
    {
        await SetCategories(SaKey);
        await SeedCard(Tsuyoi, 0, FsrsState.Mastered);

        (await GetKnownState(Tsuyosa, 0))
            .Should().BeEquivalentTo([KnownState.Mastered, KnownState.Redundant]);
    }

    [Fact]
    public async Task DueBaseCard_DoesNotMakeTheDerivedFormDue()
    {
        await SetCategories(SaKey);
        await SeedCard(Tsuyoi, 0, FsrsState.Review, due: DateTime.UtcNow.AddDays(-1),
                       lastReview: DateTime.UtcNow.AddDays(-30));

        var states = await GetKnownState(Tsuyosa, 0);

        states.Should().Contain(KnownState.Redundant);
        states.Should().Contain(KnownState.Mature);
        states.Should().NotContain(KnownState.Due);
    }

    [Fact]
    public async Task DisablingTheCategory_PutsTheDerivedFormBack()
    {
        await SetCategories(SaKey);
        await SeedCard(Tsuyoi, 0, FsrsState.Mastered);
        (await GetKnownState(Tsuyosa, 0)).Should().Contain(KnownState.Redundant);

        await SetCategories();

        (await GetKnownState(Tsuyosa, 0)).Should().BeEquivalentTo([KnownState.New]);
    }

    [Fact]
    public async Task CoverageIsTransitiveThroughTheBaseWord()
    {
        await SetCategories(SaKey, GaruKey);
        await SeedCard(Tsuyogaru, 0, FsrsState.Mastered);

        (await GetKnownState(Tsuyosa, 0))
            .Should().BeEquivalentTo([KnownState.Mastered, KnownState.Redundant]);
    }

    [Fact]
    public async Task TransitiveCoverageStopsWhenTheLinkingCategoryIsOff()
    {
        await SetCategories(SaKey);
        await SeedCard(Tsuyogaru, 0, FsrsState.Mastered);

        (await GetKnownState(Tsuyosa, 0)).Should().BeEquivalentTo([KnownState.New]);
    }

    [Fact]
    public async Task KanaBaseCard_DoesNotCoverTheKanjiDerivedForm()
    {
        await SetCategories(SaKey);
        await SeedCard(Tsuyoi, 1, FsrsState.Mastered);

        (await GetKnownState(Tsuyosa, 0)).Should().BeEquivalentTo([KnownState.New]);
        (await GetKnownState(Tsuyosa, 1))
            .Should().BeEquivalentTo([KnownState.Mastered, KnownState.Redundant]);
    }

    [Fact]
    public async Task OwnCardWins_OverTheDerivationCover()
    {
        await SetCategories(SaKey);
        await SeedCard(Tsuyoi, 0, FsrsState.Mastered);
        await SeedCard(Tsuyosa, 0, FsrsState.Blacklisted);

        (await GetKnownState(Tsuyosa, 0)).Should().BeEquivalentTo([KnownState.Blacklisted]);
    }

    [Fact]
    public async Task MasteredWordSetMember_CoversItsDerivations()
    {
        await SetCategories(SaKey);

        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var set = new WordSet { Name = "Test set", Slug = "test-set" };
            jitenDb.WordSets.Add(set);
            await jitenDb.SaveChangesAsync();

            jitenDb.WordSetMembers.Add(new WordSetMember { SetId = set.SetId, WordId = Tsuyoi, ReadingIndex = 0 });
            await jitenDb.SaveChangesAsync();

            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserWordSetStates.Add(new UserWordSetState
            {
                UserId = TestUsers.UserA, SetId = set.SetId, State = WordSetStateType.Mastered
            });
            await userDb.SaveChangesAsync();
        }

        (await GetKnownState(Tsuyosa, 0))
            .Should().BeEquivalentTo([KnownState.Mastered, KnownState.Redundant]);
    }

    [Fact]
    public async Task UnknownCategoryKeys_AreDroppedOnSave()
    {
        await SetCategories(SaKey, "not_a_category", "transitivity_pair");

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-settings").WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var settings = await response.Content.ReadFromJsonAsync<StudySettingsDto>();
        settings!.DerivationalRedundancyCategories.Should().BeEquivalentTo([SaKey]);
    }

    [Fact]
    public async Task ChangingTheSetting_MarksCoverageDirty()
    {
        await SetCategories();

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.UserMetadatas.ExecuteDeleteAsync();
        }

        await SetCategories(SaKey);

        using var check = factory.Services.CreateScope();
        var metadata = await check.ServiceProvider.GetRequiredService<UserDbContext>()
                                  .UserMetadatas.AsNoTracking()
                                  .FirstOrDefaultAsync(m => m.UserId == TestUsers.UserA);

        metadata!.CoverageDirty.Should().BeTrue();
    }

    [Fact]
    public async Task CategoriesEndpoint_ReportsGroupsWithLivePairCounts()
    {
        var response = await _client.GetAsync("/api/derivations/categories");
        response.EnsureSuccessStatusCode();

        var groups = await response.Content.ReadFromJsonAsync<List<DerivationCategoryGroupDto>>() ?? [];

        groups.Should().Contain(g => g.Key == "nominalisation");

        var saCategory = groups.SelectMany(g => g.Categories).Single(c => c.Key == SaKey);
        saCategory.PairCount.Should().Be(1);
        saCategory.ExampleBase.Should().NotBeEmpty();
        saCategory.Explanation.Should().NotBeEmpty();
    }

    [Fact]
    public async Task PairsEndpoint_ListsTheGroupMappings()
    {
        var response = await _client.GetAsync("/api/derivations/pairs?group=nominalisation");
        response.EnsureSuccessStatusCode();

        var pairs = await response.Content.ReadFromJsonAsync<List<DerivationPairDto>>() ?? [];

        var pair = pairs.Should().ContainSingle().Which;
        pair.BaseWordId.Should().Be(Tsuyoi);
        pair.BaseText.Should().Be("強[つよ]い");
        pair.DerivedWordId.Should().Be(Tsuyosa);
        pair.DerivedText.Should().Be("強[つよ]さ");
        pair.CategoryLabel.Should().NotBeEmpty();
        pair.Bidirectional.Should().BeTrue();
    }

    [Fact]
    public async Task KnownWordAmounts_CountDerivationCoveredWords()
    {
        await SetCategories(SaKey);
        await SeedCard(Tsuyoi, 0, FsrsState.Mastered);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/known-ids/amount")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var amounts = await response.Content.ReadFromJsonAsync<KnownWordAmountDto>();

        // The kanji base covers both the kanji and the kana form of 強さ; one previously unknown word.
        amounts!.DerivationCovered.Should().Be(1);
        amounts.DerivationCoveredForm.Should().Be(2);
    }

    [Fact]
    public async Task KnownWordAmounts_ReportNoDerivationCoverage_WhenTheFeatureIsOff()
    {
        await SetCategories();
        await SeedCard(Tsuyoi, 0, FsrsState.Mastered);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/user/vocabulary/known-ids/amount")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var amounts = await response.Content.ReadFromJsonAsync<KnownWordAmountDto>();

        amounts!.DerivationCovered.Should().Be(0);
        amounts.DerivationCoveredForm.Should().Be(0);
    }

    [Fact]
    public async Task PersonalSummary_ReportsMarginalGroupCounts()
    {
        await SetCategories(SaKey, "na_sa", "mi_nominal");
        await SeedCard(Tsuyoi, 0, FsrsState.Mastered);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/derivations/personal-summary")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var summary = await response.Content.ReadFromJsonAsync<DerivationPersonalSummaryDto>();

        summary!.TotalCoveredWords.Should().Be(1);

        var nominalisation = summary.Groups.Single(g => g.Key == "nominalisation");
        nominalisation.Enabled.Should().BeTrue();
        nominalisation.CoveredWords.Should().Be(1);

        var garu = summary.Groups.Single(g => g.Key == "garu");
        garu.Enabled.Should().BeFalse();
        garu.CoveredWords.Should().Be(1);
    }

    [Fact]
    public async Task PairsEndpoint_RejectsUnknownGroups()
    {
        var response = await _client.GetAsync("/api/derivations/pairs?group=not_a_group");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WordPage_ListsTheDerivationLinksBothWays()
    {
        var derived = await _client.GetFromJsonAsync<WordDto>($"/api/vocabulary/{Tsuyosa}/0/info");
        var baseWord = await _client.GetFromJsonAsync<WordDto>($"/api/vocabulary/{Tsuyoi}/0/info");

        derived!.DerivedFrom.Should().ContainSingle().Which.WordId.Should().Be(Tsuyoi);
        derived.Derives.Should().BeNullOrEmpty();

        baseWord!.Derives.Should().NotBeNull();
        baseWord.Derives!.Select(d => d.WordId).Should().Contain([Tsuyosa, Tsuyogaru]);
    }

    [Fact]
    public async Task WordPage_HidesLinksInCategoriesNoUserCanEnable()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            jitenDb.WordDerivations.Add(new JmDictWordDerivation
            {
                BaseWordId = Tsuyosa, BaseReadingIndex = 0, DerivedWordId = Tsuyogaru, DerivedReadingIndex = 0,
                Category = DerivationCategory.TransitivityPair, Direction = DerivationDirection.Bidirectional,
                Source = DerivationSource.Curated
            });
            await jitenDb.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<IDerivationLinkCache>().Reload();
        }

        var derived = await _client.GetFromJsonAsync<WordDto>($"/api/vocabulary/{Tsuyosa}/0/info");

        derived!.Derives.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task OmittedCategories_LeaveTheStoredSelectionAlone()
    {
        await SetCategories(SaKey);

        var request = new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
                      .WithUser(TestUsers.UserA)
                      .WithJsonContent(new { newCardsPerDay = 15 });
        (await _client.SendAsync(request)).EnsureSuccessStatusCode();

        var read = new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-settings").WithUser(TestUsers.UserA);
        var settings = await (await _client.SendAsync(read)).Content.ReadFromJsonAsync<StudySettingsDto>();

        settings!.DerivationalRedundancyCategories.Should().BeEquivalentTo([SaKey]);
    }

    private async Task SubscribeToWordSet(WordSetStateType state, int wordId, byte readingIndex)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var set = new WordSet { Name = $"Set {Guid.NewGuid():N}", Slug = Guid.NewGuid().ToString("N") };
        jitenDb.WordSets.Add(set);
        await jitenDb.SaveChangesAsync();

        jitenDb.WordSetMembers.Add(new WordSetMember
        {
            SetId = set.SetId, WordId = wordId, ReadingIndex = readingIndex
        });
        await jitenDb.SaveChangesAsync();

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserWordSetStates.Add(new UserWordSetState
        {
            UserId = TestUsers.UserA, SetId = set.SetId, State = state
        });
        await userDb.SaveChangesAsync();
    }

    private async Task SeedStaticStudyDeck(params (int WordId, short ReadingIndex)[] words)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var deck = new UserStudyDeck
        {
            UserId = TestUsers.UserA, DeckType = StudyDeckType.StaticWordList, Name = "Static", IsActive = true
        };
        userDb.UserStudyDecks.Add(deck);
        await userDb.SaveChangesAsync();

        var sortOrder = 0;
        foreach (var (wordId, readingIndex) in words)
            userDb.UserStudyDeckWords.Add(new UserStudyDeckWord
            {
                UserStudyDeckId = deck.UserStudyDeckId, WordId = wordId, ReadingIndex = readingIndex,
                SortOrder = sortOrder++
            });

        await userDb.SaveChangesAsync();
    }

    private async Task<List<(int WordId, byte ReadingIndex)>> GetNewCards()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-batch?limit=20")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("cards").EnumerateArray()
                   .Where(c => c.GetProperty("isNewCard").GetBoolean())
                   .Select(c => (c.GetProperty("wordId").GetInt32(), c.GetProperty("readingIndex").GetByte()))
                   .ToList();
    }

    [Fact]
    public async Task BlacklistedWordSetBase_KeepsItsDerivedFormOutOfNewCardSelection()
    {
        await SetCategories(SaKey);
        await SubscribeToWordSet(WordSetStateType.Blacklisted, Tsuyoi, 0);
        await SeedStaticStudyDeck((Tsuyosa, 0));

        (await GetNewCards()).Should().NotContain((Tsuyosa, (byte)0));
    }

    [Fact]
    public async Task BlacklistedWordSetBase_LeavesTheDerivedFormAlone_WhenTheCategoryIsOff()
    {
        await SetCategories();
        await SubscribeToWordSet(WordSetStateType.Blacklisted, Tsuyoi, 0);
        await SeedStaticStudyDeck((Tsuyosa, 0));

        (await GetNewCards()).Should().Contain((Tsuyosa, (byte)0));
    }

    [Fact]
    public async Task DerivationCoverEndpoint_ReportsNothing_WhenTheFormHasItsOwnCard()
    {
        await SetCategories(SaKey);
        await SeedCard(Tsuyoi, 0, FsrsState.Mastered);
        await SeedCard(Tsuyosa, 0, FsrsState.Review);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/vocabulary/{Tsuyosa}/0/derivation-cover")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // A null cover serialises as an empty body, so there is nothing to deserialise.
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DerivationCoverEndpoint_NamesTheCoveringEntry()
    {
        await SetCategories(SaKey);
        await SeedCard(Tsuyoi, 0, FsrsState.Mastered);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/vocabulary/{Tsuyosa}/0/derivation-cover")
            .WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var cover = await response.Content.ReadFromJsonAsync<DerivationCoverDto>();

        cover.Should().NotBeNull();
        cover!.WordId.Should().Be(Tsuyoi);
        cover.Text.Should().Be("強い");
        cover.CategoryKey.Should().Be(SaKey);
    }
}
