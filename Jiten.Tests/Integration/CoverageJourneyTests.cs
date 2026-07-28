using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// Coverage journey (Jiten+). Seeds one deck of four words with a known WordCount, then drives the
/// per-deck and global endpoints through the real service so the learned-map joins are exercised.
/// The rate limiter is live in the test host: both endpoints allow 30 requests/minute per user, and
/// this class spends about half that budget as UserA.
/// </summary>
public class CoverageJourneyTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private int _deckId;
    private long _nextCardId = 1;
    private DateTime _stamp;

    private static readonly DateTime Now = DateTime.UtcNow;
    private static int _stampSeed;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        await ResetBilling();
        await ResetSrs();
        await SeedDeck();
        // Every Redis key carries the coverage stamp, so a distinct one per test keeps the shared
        // in-memory Redis from serving one test's learned map to the next.
        _stamp = Now.AddSeconds(Interlocked.Increment(ref _stampSeed));
        await SetCoverageStamp(_stamp);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Seeding / helpers --------------------------------------------------

    private async Task ResetBilling()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserPromoCredits.RemoveRange(userDb.UserPromoCredits);
        userDb.PromoCodes.RemoveRange(userDb.PromoCodes);
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeSubscriptionActive = false;
            user.SubscriptionPeriodEnd = null;
            user.SubscriptionPlan = null;
            user.IsLifetime = false;
            user.LifetimeSource = null;
            user.AdminPremiumOverride = false;
        }

        await userDb.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            jitenPlus.InvalidateTier(id);
    }

    private async Task ResetSrs()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.UserWordSetStates.ExecuteDeleteAsync();
        await userDb.UserMetadatas.ExecuteDeleteAsync();
        await userDb.UserCoverageChunks.ExecuteDeleteAsync();

        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await jitenDb.WordSetMembers.ExecuteDeleteAsync();
        await jitenDb.WordSets.ExecuteDeleteAsync();

        // The growth series, the transition dates and the in-process segment map carry no rotating stamp,
        // so they have to be cleared explicitly between tests.
        var redis = factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
        {
            await redis.KeyDeleteAsync(CoverageJourneyService.GrowthCacheKeyPrefix + id);
            await redis.KeyDeleteAsync($"srsdates:{id}");
        }

        (factory.Services.GetRequiredService<IMemoryCache>() as MemoryCache)?.Clear();

        _nextCardId = 1;
    }

    private async Task SeedDeck()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        await jitenDb.DeckWords.ExecuteDeleteAsync();
        await jitenDb.WordForms.ExecuteDeleteAsync();
        await jitenDb.JMDictWords.ExecuteDeleteAsync();

        for (var i = 1; i <= 4; i++)
        {
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = i, PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.Add(new JmDictWordForm
            {
                WordId = i, ReadingIndex = 0, Text = $"漢字{i}", RubyText = $"かんじ{i}", FormType = JmDictFormType.KanjiForm
            });
            jitenDb.WordForms.Add(new JmDictWordForm
            {
                WordId = i, ReadingIndex = 1, Text = $"かんじ{i}", RubyText = $"かんじ{i}", FormType = JmDictFormType.KanaForm
            });
        }

        var deck = new Deck
        {
            OriginalTitle = "Journey Novel",
            MediaType = MediaType.Novel,
            ReleaseDate = new DateOnly(2020, 1, 1),
            CharacterCount = 1000,
            WordCount = 100,
            UniqueWordCount = 4
        };
        jitenDb.Decks.Add(deck);
        await jitenDb.SaveChangesAsync();
        _deckId = deck.DeckId;

        // 40 / 30 / 20 / 10 occurrences, so coverage figures are exact whole percentages.
        var occurrences = new[] { 40, 30, 20, 10 };
        for (var i = 0; i < occurrences.Length; i++)
            jitenDb.DeckWords.Add(new DeckWord { Deck = deck, WordId = i + 1, ReadingIndex = 0, Occurrences = occurrences[i] });

        await jitenDb.SaveChangesAsync();
    }

    private async Task SetCoverageStamp(DateTime stamp)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var existing = await userDb.UserMetadatas.FirstOrDefaultAsync(m => m.UserId == TestUsers.UserA);
        if (existing == null)
            userDb.UserMetadatas.Add(new UserMetadata { UserId = TestUsers.UserA, CoverageRefreshedAt = stamp });
        else
            existing.CoverageRefreshedAt = stamp;

        await userDb.SaveChangesAsync();
    }

    private async Task MakeTrial(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var code = new PromoCode { Code = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), DurationDays = 5, GrantsFullTier = false };
        userDb.PromoCodes.Add(code);
        await userDb.SaveChangesAsync();
        userDb.UserPromoCredits.Add(new UserPromoCredit
        {
            UserId = userId, PromoCodeId = code.CodeId, GrantsFullTier = false, RemainingDays = 5, GrantedAt = DateTime.UtcNow
        });
        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(userId);
    }

    /// <summary>Adds a card whose scheduling interval makes it mature or young, first reviewed on <paramref name="firstReview"/>.</summary>
    private async Task AddCard(int wordId, byte readingIndex, DateTime firstReview, bool mature, string userId = TestUsers.UserA)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var lastReview = firstReview.AddDays(1);
        var cardId = _nextCardId++;
        userDb.FsrsCards.Add(new FsrsCard
        {
            CardId = cardId,
            UserId = userId,
            WordId = wordId,
            ReadingIndex = readingIndex,
            State = FsrsState.Review,
            Due = lastReview.AddDays(mature ? 40 : 3),
            LastReview = lastReview,
            CreatedAt = firstReview.AddDays(-400)
        });
        userDb.FsrsReviewLogs.Add(new FsrsReviewLog(cardId, FsrsRating.Good, firstReview));
        userDb.FsrsReviewLogs.Add(new FsrsReviewLog(cardId, FsrsRating.Good, lastReview));
        await userDb.SaveChangesAsync();
    }

    /// <summary>Seeds a card with an explicit review timeline; <paramref name="finalIntervalDays"/> sets the live Due - LastReview gap.</summary>
    private async Task AddCardWithReviews(int wordId, byte readingIndex, DateTime[] reviews, int finalIntervalDays, string userId = TestUsers.UserA)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var cardId = _nextCardId++;
        var lastReview = reviews[^1];
        userDb.FsrsCards.Add(new FsrsCard
        {
            CardId = cardId,
            UserId = userId,
            WordId = wordId,
            ReadingIndex = readingIndex,
            State = FsrsState.Review,
            Due = lastReview.AddDays(finalIntervalDays),
            LastReview = lastReview,
            CreatedAt = reviews[0]
        });

        foreach (var review in reviews)
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(cardId, FsrsRating.Good, review));

        await userDb.SaveChangesAsync();
    }

    /// <summary>Adds a never-reviewed card marked known outright, which is how blacklisting a word records it.</summary>
    private async Task AddBlacklistedCard(int wordId, byte readingIndex, DateTime createdAt, string userId = TestUsers.UserA)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.Add(new FsrsCard
        {
            CardId = _nextCardId++,
            UserId = userId,
            WordId = wordId,
            ReadingIndex = readingIndex,
            State = FsrsState.Blacklisted,
            Due = createdAt,
            LastReview = null,
            CreatedAt = createdAt
        });
        await userDb.SaveChangesAsync();
    }

    /// <summary>Puts a reviewed card back in the new queue exactly as SrsController.ResetCardSchedule does, keeping its review logs.</summary>
    private async Task ResetCardSchedule(int wordId, byte readingIndex, DateTime resetAt, string userId = TestUsers.UserA)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var card = await userDb.FsrsCards.FirstAsync(c => c.UserId == userId && c.WordId == wordId && c.ReadingIndex == readingIndex);
        card.State = FsrsState.Learning;
        card.Step = 0;
        card.Stability = null;
        card.Difficulty = null;
        card.Due = resetAt;
        card.LastReview = null;
        await userDb.SaveChangesAsync();
    }

    private async Task AddWordSet(int wordId, short readingIndex, DateTime heldSince, string userId = TestUsers.UserA)
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var set = new WordSet { Slug = $"set-{wordId}-{readingIndex}", Name = "Set", WordCount = 1 };
        jitenDb.WordSets.Add(set);
        await jitenDb.SaveChangesAsync();
        jitenDb.WordSetMembers.Add(new WordSetMember { SetId = set.SetId, WordId = wordId, ReadingIndex = readingIndex, Position = 0 });
        await jitenDb.SaveChangesAsync();

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserWordSetStates.Add(new UserWordSetState
        {
            UserId = userId, SetId = set.SetId, State = WordSetStateType.Mastered, CreatedAt = heldSince
        });
        await userDb.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> GetJourney(string userId, int? deckId = null) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/media-deck/{deckId ?? _deckId}/coverage-journey").WithUser(userId));

    private Task<HttpResponseMessage> GetGrowth(string userId) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/knowledge-growth").WithUser(userId));

    private static DateOnly WeekStart(DateTime moment)
    {
        var date = DateOnly.FromDateTime(moment);
        return date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
    }

    private static async Task<JourneyDto> ReadJourney(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JourneyDto>())!;
    }

    private static async Task<GlobalGrowthDto> ReadGrowth(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<GlobalGrowthDto>())!;
    }

    // ---- Gating -------------------------------------------------------------

    [Fact]
    public async Task Journey_WithoutJitenPlus_Returns403WithUpsellPayload()
    {
        var response = await GetJourney(TestUsers.UserA);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        payload.GetProperty("jitenPlus").GetBoolean().Should().BeTrue();
        payload.GetProperty("feature").GetString().Should().Be("coverage-journey");
        payload.GetProperty("requiredTier").GetString().Should().Be("trial");
    }

    [Fact]
    public async Task Journey_OnTrialTier_IsAllowed()
    {
        await MakeTrial(TestUsers.UserA);

        var response = await GetJourney(TestUsers.UserA);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Journey_ForUnknownDeck_Returns404()
    {
        await MakeTrial(TestUsers.UserA);

        var response = await GetJourney(TestUsers.UserA, deckId: 999999);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task KnowledgeGrowth_IsFreeAndNeedsNoJitenPlus()
    {
        await AddCard(1, 0, Now.AddDays(-100), mature: true);

        var response = await GetGrowth(TestUsers.UserA);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var growth = (await response.Content.ReadFromJsonAsync<GlobalGrowthDto>())!;
        growth.Points.Should().NotBeEmpty();
    }

    // ---- Series -------------------------------------------------------------

    [Fact]
    public async Task Journey_WithNoKnownWords_ReportsNoHistory()
    {
        await MakeTrial(TestUsers.UserA);

        var journey = await ReadJourney(await GetJourney(TestUsers.UserA));

        journey.HasEnoughHistory.Should().BeFalse();
        journey.Points.Should().BeEmpty();
        journey.CurrentCoverage.Should().Be(0);
    }

    [Fact]
    public async Task Journey_DatesWordsByTheirFirstReview()
    {
        await MakeTrial(TestUsers.UserA);
        await AddCard(1, 0, Now.AddDays(-200), mature: true);
        await AddCard(2, 0, Now.AddDays(-10), mature: true);

        var journey = await ReadJourney(await GetJourney(TestUsers.UserA));

        journey.Granularity.Should().Be("weekly");
        journey.HasEnoughHistory.Should().BeTrue();
        journey.StartCoverage.Should().BeApproximately(40f, 0.01f);
        journey.CurrentCoverage.Should().BeApproximately(70f, 0.01f);
        journey.Points[^1].UniqueCoverage.Should().BeApproximately(50f, 0.01f);
        journey.Points.Select(p => p.Coverage).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Journey_SeparatesMatureFromYoung()
    {
        await MakeTrial(TestUsers.UserA);
        await AddCard(1, 0, Now.AddDays(-100), mature: true);
        await AddCard(2, 0, Now.AddDays(-50), mature: false);

        var journey = await ReadJourney(await GetJourney(TestUsers.UserA));

        journey.CurrentCoverage.Should().BeApproximately(40f, 0.01f);
        journey.Points[^1].CombinedCoverage.Should().BeApproximately(70f, 0.01f);
        journey.Points[^1].KnownWords.Should().Be(1);
        journey.Points[^1].KnownWordsCombined.Should().Be(2);
    }

    [Fact]
    public async Task Journey_ExpandsKanaFormsOfAMatureKanjiCard()
    {
        await MakeTrial(TestUsers.UserA);
        await AddCard(1, 0, Now.AddDays(-100), mature: true);

        using (var scope = factory.Services.CreateScope())
        {
            // The deck's word 2 is only known through the kana form of its kanji card.
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var deckWord = await jitenDb.DeckWords.FirstAsync(dw => dw.DeckId == _deckId && dw.WordId == 2);
            deckWord.ReadingIndex = 1;
            await jitenDb.SaveChangesAsync();
        }

        await AddCard(2, 0, Now.AddDays(-30), mature: true);

        var journey = await ReadJourney(await GetJourney(TestUsers.UserA));

        journey.CurrentCoverage.Should().BeApproximately(70f, 0.01f);
    }

    [Fact]
    public async Task Journey_CountsAWordOnceWhenTwoKanjiCardsExpandOntoIt()
    {
        await MakeTrial(TestUsers.UserA);

        using (var scope = factory.Services.CreateScope())
        {
            // Word 2 gets a second kanji form and its kana form moves to index 2, which is what the deck writes.
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var kana = await jitenDb.WordForms.FirstAsync(f => f.WordId == 2 && f.ReadingIndex == 1);
            kana.FormType = JmDictFormType.KanjiForm;
            kana.Text = "漢字2b";
            jitenDb.WordForms.Add(new JmDictWordForm
            {
                WordId = 2, ReadingIndex = 2, Text = "かんじ2", RubyText = "かんじ2", FormType = JmDictFormType.KanaForm
            });

            var deckWord = await jitenDb.DeckWords.FirstAsync(dw => dw.DeckId == _deckId && dw.WordId == 2);
            deckWord.ReadingIndex = 2;
            await jitenDb.SaveChangesAsync();
        }

        await AddCard(2, 0, Now.AddDays(-100), mature: true);
        await AddCard(2, 1, Now.AddDays(-30), mature: true);

        var journey = await ReadJourney(await GetJourney(TestUsers.UserA));

        journey.CurrentCoverage.Should().BeApproximately(30f, 0.01f);
        journey.Points[^1].KnownWords.Should().Be(1);
        // Held from the earlier of the two cards, and never counted twice in between.
        journey.Points.Select(p => p.Coverage).Should().AllBeEquivalentTo(30f);
    }

    [Fact]
    public async Task Journey_CountsWordSetMembershipFromWhenTheSetWasHeld()
    {
        await MakeTrial(TestUsers.UserA);
        await AddWordSet(3, 0, Now.AddDays(-100));

        var journey = await ReadJourney(await GetJourney(TestUsers.UserA));

        journey.CurrentCoverage.Should().BeApproximately(20f, 0.01f);
        journey.StartDate.Should().Be(WeekStart(Now.AddDays(-100)));
    }

    [Fact]
    public async Task Journey_TakesTheEarliestDateWhenAWordIsReachableTwoWays()
    {
        await MakeTrial(TestUsers.UserA);
        // Word 1 is mature via its kanji card; word 2's kana form is reachable both by expansion and a word set.
        await AddCard(2, 0, Now.AddDays(-30), mature: true);
        await AddWordSet(2, 1, Now.AddDays(-200));

        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var deckWord = await jitenDb.DeckWords.FirstAsync(dw => dw.DeckId == _deckId && dw.WordId == 2);
            deckWord.ReadingIndex = 1;
            await jitenDb.SaveChangesAsync();
        }

        var journey = await ReadJourney(await GetJourney(TestUsers.UserA));

        // The word set's date wins, so the series opens 200 days back already carrying the word.
        journey.StartDate.Should().Be(WeekStart(Now.AddDays(-200)));
        journey.StartCoverage.Should().BeApproximately(30f, 0.01f);
        journey.CurrentCoverage.Should().BeApproximately(30f, 0.01f);
    }

    [Fact]
    public async Task Journey_CountsAWordYoungUntilItsIntervalCrossed()
    {
        await MakeTrial(TestUsers.UserA);
        // Gaps of 5, 5 and 20 days stay under the maturity line; the final 40-day interval crosses it.
        await AddCardWithReviews(1, 0,
        [
            Now.AddDays(-60), Now.AddDays(-55), Now.AddDays(-50), Now.AddDays(-30)
        ], finalIntervalDays: 40);

        var journey = await ReadJourney(await GetJourney(TestUsers.UserA));

        var early = journey.Points.Last(p => p.Date <= DateOnly.FromDateTime(Now.AddDays(-40)));
        early.Coverage.Should().Be(0f);
        early.CombinedCoverage.Should().BeApproximately(40f, 0.01f);

        journey.CurrentCoverage.Should().BeApproximately(40f, 0.01f);
    }

    [Fact]
    public async Task Journey_DeckCoverageFallsWhenAWordLapses()
    {
        await MakeTrial(TestUsers.UserA);
        // Mature across a 60-day interval, then lapsed back to daily reviews.
        await AddCardWithReviews(1, 0,
        [
            Now.AddDays(-200), Now.AddDays(-140), Now.AddDays(-10), Now.AddDays(-9)
        ], finalIntervalDays: 2);

        var journey = await ReadJourney(await GetJourney(TestUsers.UserA));

        var whileMature = journey.Points.Last(p => p.Date <= DateOnly.FromDateTime(Now.AddDays(-100)));
        whileMature.Coverage.Should().BeApproximately(40f, 0.01f);

        // Parity with the coverage bar: the card is young today, so mature coverage ends at zero.
        journey.CurrentCoverage.Should().Be(0f);
        journey.Points[^1].CombinedCoverage.Should().BeApproximately(40f, 0.01f);
    }

    [Fact]
    public async Task Journey_DropsAWordWhoseScheduleWasReset()
    {
        await MakeTrial(TestUsers.UserA);
        await AddCardWithReviews(1, 0, [Now.AddDays(-200), Now.AddDays(-140)], finalIntervalDays: 60);
        await ResetCardSchedule(1, 0, Now.AddDays(-30));

        var journey = await ReadJourney(await GetJourney(TestUsers.UserA));

        var whileMature = journey.Points.Last(p => p.Date <= DateOnly.FromDateTime(Now.AddDays(-100)));
        whileMature.Coverage.Should().BeApproximately(40f, 0.01f);

        // Parity with the coverage bars: a reset card is back in the new queue, so it is neither mature nor young.
        journey.CurrentCoverage.Should().Be(0f);
        journey.Points[^1].CombinedCoverage.Should().Be(0f);
    }

    [Fact]
    public async Task Journey_IsPerUser()
    {
        await MakeTrial(TestUsers.UserA);
        await MakeTrial(TestUsers.UserB);
        await AddCard(1, 0, Now.AddDays(-100), mature: true);

        var journeyB = await ReadJourney(await GetJourney(TestUsers.UserB));

        journeyB.CurrentCoverage.Should().Be(0);
        journeyB.HasEnoughHistory.Should().BeFalse();
    }

    // ---- Caching ------------------------------------------------------------

    [Fact]
    public async Task Journey_IsServedFromCacheUntilCoverageIsRefreshed()
    {
        await MakeTrial(TestUsers.UserA);
        await AddCard(1, 0, Now.AddDays(-100), mature: true);

        var first = await ReadJourney(await GetJourney(TestUsers.UserA));
        first.CurrentCoverage.Should().BeApproximately(40f, 0.01f);

        await AddCard(2, 0, Now.AddDays(-50), mature: true);

        var cached = await ReadJourney(await GetJourney(TestUsers.UserA));
        cached.CurrentCoverage.Should().BeApproximately(40f, 0.01f);

        await SetCoverageStamp(_stamp.AddMinutes(1));

        var refreshed = await ReadJourney(await GetJourney(TestUsers.UserA));
        refreshed.CurrentCoverage.Should().BeApproximately(70f, 0.01f);
    }

    [Fact]
    public async Task Journey_IsCachedEvenWithoutACoverageRefresh()
    {
        // Admin gets no UserMetadata row from the seed, so this drives the unstamped cache path:
        // with no stamp to rotate the key, an uncached response would recompute on every request.
        await MakeTrial(TestUsers.Admin);
        await AddCard(1, 0, Now.AddDays(-100), mature: true, userId: TestUsers.Admin);

        var first = await ReadJourney(await GetJourney(TestUsers.Admin));
        first.AsOf.Should().BeNull();
        first.CurrentCoverage.Should().BeApproximately(40f, 0.01f);

        await AddCard(2, 0, Now.AddDays(-50), mature: true, userId: TestUsers.Admin);

        var second = await ReadJourney(await GetJourney(TestUsers.Admin));
        second.CurrentCoverage.Should().BeApproximately(40f, 0.01f);
    }

    // ---- Global growth ------------------------------------------------------

    [Fact]
    public async Task KnowledgeGrowth_AccumulatesKnownWordsOverTime()
    {
        await AddCard(1, 0, Now.AddDays(-100), mature: true);
        await AddCard(2, 0, Now.AddDays(-50), mature: false);

        var response = await GetGrowth(TestUsers.UserA);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var growth = (await response.Content.ReadFromJsonAsync<GlobalGrowthDto>())!;

        growth.HasEnoughHistory.Should().BeTrue();
        // One card per state, counted once each: kana expansion belongs to coverage, not to a word count.
        growth.Points[^1].KnownWords.Should().Be(1);
        growth.Points[^1].KnownWordsCombined.Should().Be(2);
    }

    [Fact]
    public async Task KnowledgeGrowth_CountsACardAsYoungWhileItsIntervalWasShort()
    {
        // Gaps of 5, 5 and 20 days keep it under the 21-day maturity line until the final 40-day interval.
        await AddCardWithReviews(1, 0,
        [
            Now.AddDays(-60), Now.AddDays(-55), Now.AddDays(-50), Now.AddDays(-30)
        ], finalIntervalDays: 40);

        var growth = await ReadGrowth(await GetGrowth(TestUsers.UserA));

        var early = growth.Points.Last(p => p.Date <= DateOnly.FromDateTime(Now.AddDays(-40)));
        early.KnownWords.Should().Be(0);
        early.KnownWordsCombined.Should().Be(1);

        growth.Points[^1].KnownWords.Should().Be(1);
        growth.Points[^1].KnownWordsCombined.Should().Be(1);
    }

    [Fact]
    public async Task KnowledgeGrowth_DeclinesWhenAMatureCardLapses()
    {
        // Mature for months, then a lapse drops it back to daily reviews.
        await AddCardWithReviews(1, 0,
        [
            Now.AddDays(-200), Now.AddDays(-140), Now.AddDays(-10), Now.AddDays(-9)
        ], finalIntervalDays: 2);

        var growth = await ReadGrowth(await GetGrowth(TestUsers.UserA));

        var whileMature = growth.Points.First(p => p.Date >= DateOnly.FromDateTime(Now.AddDays(-120)));
        whileMature.KnownWords.Should().Be(1);

        // The whole point of the real-state series: it is allowed to go back down.
        growth.Points[^1].KnownWords.Should().Be(0);
        growth.Points[^1].KnownWordsCombined.Should().Be(1);
        growth.Points.Select(p => p.KnownWords).Should().NotBeInAscendingOrder();
    }

    [Fact]
    public async Task KnowledgeGrowth_CountsAWordOnceWhenSeveralOfItsFormsAreStudied()
    {
        await AddCard(1, 0, Now.AddDays(-100), mature: true);
        await AddCard(1, 1, Now.AddDays(-90), mature: false);
        await AddCard(2, 0, Now.AddDays(-80), mature: true);

        var growth = await ReadGrowth(await GetGrowth(TestUsers.UserA));

        // Two words, three cards; the young card cannot pull its own word out of the mature count either.
        growth.Points[^1].KnownWords.Should().Be(2);
        growth.Points[^1].KnownWordsCombined.Should().Be(2);
    }

    [Fact]
    public async Task KnowledgeGrowth_ExcludesBlacklistedWords()
    {
        await AddCard(1, 0, Now.AddDays(-100), mature: true);
        await AddBlacklistedCard(2, 0, Now.AddDays(-90));

        var growth = await ReadGrowth(await GetGrowth(TestUsers.UserA));

        growth.Points[^1].KnownWords.Should().Be(1);
        growth.Points[^1].KnownWordsCombined.Should().Be(1);
    }

    [Fact]
    public async Task KnowledgeGrowth_WithNoCards_IsEmpty()
    {
        var response = await GetGrowth(TestUsers.UserA);
        var growth = (await response.Content.ReadFromJsonAsync<GlobalGrowthDto>())!;

        growth.Points.Should().BeEmpty();
        growth.HasEnoughHistory.Should().BeFalse();
    }
}
