using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Authentication;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class SiteStatsJobTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const int LearnerCount = SiteStatsBuilder.MinLearnersPerSection + 2;
    private static readonly DateTime Now = DateTime.UtcNow;
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now);

    private readonly HttpClient _client = factory.CreateClient();
    private static readonly string[] Learners = Enumerable.Range(0, LearnerCount).Select(i => $"5175a75e-0000-0000-0000-{i:D12}").ToArray();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        factory.Services.GetRequiredService<IMemoryCache>().Remove(Jiten.Api.Controllers.StatsController.SiteStatsCacheKey);
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await jitenDb.SiteStatsSnapshots.ExecuteDeleteAsync();
        await jitenDb.WordSetMembers.ExecuteDeleteAsync();
        await jitenDb.WordSets.ExecuteDeleteAsync();
        await jitenDb.WordFormFrequencies.ExecuteDeleteAsync();
        await jitenDb.WordForms.ExecuteDeleteAsync();
        await jitenDb.Definitions.ExecuteDeleteAsync();
        await jitenDb.JMDictWords.ExecuteDeleteAsync();

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.UserReviewDailies.ExecuteDeleteAsync();
        await userDb.UserWordSetStates.ExecuteDeleteAsync();
        await userDb.UserAccomplishments.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();
        await userDb.Users.Where(u => Learners.Contains(u.Id)).ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpResponseMessage> GetSiteStats()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/stats/site");
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return await _client.SendAsync(request);
    }

    private async Task<SiteStatsDto> RunSnapshot()
    {
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SiteStatsJob>().Snapshot();

        var response = await GetSiteStats();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SiteStatsDto>(SiteStatsJob.PayloadJson))!;
    }

    private async Task SeedLearners()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        // UserDbContext stamps CreatedAt on save, so every seeded learner signs up today.
        var signup = Now;
        for (var i = 0; i < LearnerCount; i++)
        {
            var id = Learners[i];
            userDb.Users.Add(new User { Id = id, UserName = id, CreatedAt = signup, UpdatedAt = signup });
            userDb.UserReviewDailies.Add(new UserReviewDaily
            {
                UserId = id, LocalDate = Today, ReviewCount = 10 * (i + 1), CorrectCount = 5, NewCardCount = 2, TotalDurationMs = 3_600_000
            });
            userDb.UserReviewDailies.Add(new UserReviewDaily
            {
                UserId = id, LocalDate = Today.AddDays(-400), ReviewCount = 1000, NewCardCount = 50, TotalDurationMs = 3_600_000
            });
            for (var w = 0; w < 3; w++)
                userDb.FsrsCards.Add(new FsrsCard(id, 1000 + w, 0, state: FsrsState.Review, stability: 30, difficulty: 5));
            userDb.UserFsrsSettings.Add(new Jiten.Core.Data.User.UserFsrsSettings { UserId = id, SettingsJson = """{"timezone":"Asia/Tokyo"}""" });
        }

        // Learner 0 blacklisted the particle 50 by card and mastered its set; the card decides, the set still adds 51.
        userDb.FsrsCards.Add(new FsrsCard(Learners[0], 50, 0, state: FsrsState.Blacklisted));

        var particles = new WordSet { Slug = "particle-grammar", Name = "Particles" };
        var names = new WordSet { Slug = "names", Name = "Names" };
        jitenDb.WordSets.AddRange(particles, names);
        await jitenDb.SaveChangesAsync();
        jitenDb.WordSetMembers.AddRange(
            new WordSetMember { SetId = particles.SetId, WordId = 50, ReadingIndex = 0 },
            new WordSetMember { SetId = particles.SetId, WordId = 51, ReadingIndex = 0 },
            new WordSetMember { SetId = names.SetId, WordId = 60, ReadingIndex = 0 });
        userDb.UserWordSetStates.Add(new UserWordSetState { UserId = Learners[0], SetId = particles.SetId, State = WordSetStateType.Mastered });
        userDb.UserWordSetStates.Add(new UserWordSetState { UserId = Learners[1], SetId = names.SetId, State = WordSetStateType.Mastered });

        foreach (var (wordId, rank) in new[] { (1000, 1), (1001, 2), (1002, 3), (50, 4), (51, 5) })
            jitenDb.WordFormFrequencies.Add(new JmDictWordFormFrequency { WordId = wordId, ReadingIndex = 0, FrequencyRank = rank, UsedInMediaAmount = 1 });

        await jitenDb.SaveChangesAsync();
        await userDb.SaveChangesAsync();
    }

    [Fact]
    public async Task Endpoint_returns_204_before_the_first_snapshot()
    {
        (await GetSiteStats()).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Totals_count_reviews_from_signup_and_known_words_from_cards_and_vocabulary_sets()
    {
        await SeedLearners();

        var stats = await RunSnapshot();

        stats.Date.Should().Be(Today);
        stats.Totals.Learners.Should().Be(LearnerCount);
        stats.Totals.Reviews.Should().Be(Enumerable.Range(1, LearnerCount).Sum(i => 10L * i));
        stats.Totals.HoursReviewing.Should().Be(LearnerCount);
        // 3 cards each, plus learner 0's word 51 from the particle set; the name set never counts.
        stats.Totals.WordsKnown.Should().Be(3L * LearnerCount + 1);

        stats.Recent.PeopleReviewing.Should().Be(LearnerCount);
        stats.Recent.NewWords.Should().Be(2L * LearnerCount);
        stats.Recent.MedianReviewsPerStudyDay.Should().Be(SiteStatsBuilder.Percentile(Enumerable.Range(1, LearnerCount).Select(i => 10.0 * i).ToList(), 0.5));

        stats.LearnersByMonth.Should().ContainSingle().Which.Total.Should().Be(LearnerCount);

        stats.KnownWords!.Median.Should().Be(3);
        stats.KnownWords.AtLeast.Should().OnlyContain(a => a.Share == 0);
    }

    [Fact]
    public async Task Frequency_band_leaves_each_learners_blacklisted_words_out_of_their_own_band()
    {
        await SeedLearners();

        var band = (await RunSnapshot()).FrequencyBands.Should().ContainSingle().Subject;

        band.From.Should().Be(1);
        band.Median.Should().Be(60);
        band.P75.Should().Be(60);
        band.P90.Should().Be(60);
    }

    [Fact]
    public async Task Heatmap_buckets_reviews_in_each_reviewers_timezone()
    {
        await SeedLearners();
        var reviewedAt = new DateTime(Today.Year, Today.Month, Today.Day, 10, 15, 0, DateTimeKind.Utc).AddDays(-3);
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            foreach (var card in await userDb.FsrsCards.Where(c => c.WordId == 1000).ToListAsync())
                userDb.FsrsReviewLogs.Add(new FsrsReviewLog(card.CardId, FsrsRating.Good, reviewedAt) { State = FsrsState.Review });
            await userDb.SaveChangesAsync();
        }

        var heatmap = (await RunSnapshot()).Heatmap!;

        var tokyo = TimeZoneInfo.ConvertTimeFromUtc(reviewedAt, TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"));
        heatmap.Learners.Should().Be(LearnerCount);
        heatmap.Reviews[((int)tokyo.DayOfWeek + 6) % 7][tokyo.Hour].Should().Be(LearnerCount);
        heatmap.Reviews.Sum(day => day.Sum()).Should().Be(LearnerCount);
    }

    [Fact]
    public async Task Forgotten_words_need_enough_learners_and_reviews()
    {
        await SeedLearners();
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = 1000, PartsOfSpeech = ["v5r"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 1000, ReadingIndex = 0, Text = "抑える", RubyText = "抑[おさ]える", FormType = JmDictFormType.KanjiForm });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 1000, ReadingIndex = 1, Text = "おさえる", RubyText = "おさえる", FormType = JmDictFormType.KanaForm });
            jitenDb.Definitions.Add(new JmDictDefinition { WordId = 1000, SenseIndex = 0, EnglishMeanings = ["to restrain", "to hold down", "to suppress"], PartsOfSpeech = ["v5r"] });
            await jitenDb.SaveChangesAsync();

            var at = Now.AddDays(-5);
            foreach (var card in await userDb.FsrsCards.Where(c => c.WordId == 1000 || c.WordId == 1001).ToListAsync())
            {
                // Word 1000: 10 reviews per learner, 4 missed. Word 1001: one review each, under the 200-review floor.
                var reviews = card.WordId == 1000 ? 10 : 1;
                for (var r = 0; r < reviews; r++)
                    userDb.FsrsReviewLogs.Add(new FsrsReviewLog(card.CardId, r < 4 ? FsrsRating.Again : FsrsRating.Good, at.AddMinutes(r)) { State = FsrsState.Review });
            }

            await userDb.SaveChangesAsync();
        }

        var word = (await RunSnapshot()).Forgotten.Should().ContainSingle().Subject;

        word.WordId.Should().Be(1000);
        word.Text.Should().Be("抑える");
        word.ReadingFurigana.Should().Be("抑[おさ]える");
        word.Gloss.Should().Be("to restrain; to hold down");
        word.MissedPercent.Should().Be(40);
    }

    [Fact]
    public async Task Studied_titles_fold_volumes_into_their_parent_and_drop_titles_under_the_floor()
    {
        await SeedLearners();
        using (var scope = factory.Services.CreateScope())
        {
            var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            jitenDb.Decks.AddRange(
                new Deck { DeckId = 10, OriginalTitle = "よつばと！", MediaType = MediaType.Manga, CreationDate = Now.AddYears(-1) },
                new Deck { DeckId = 11, OriginalTitle = "Volume 1", ParentDeckId = 10, MediaType = MediaType.Manga, CreationDate = Now.AddYears(-1) },
                new Deck { DeckId = 20, OriginalTitle = "Quiet", MediaType = MediaType.Anime, CreationDate = Now.AddYears(-1) });
            await jitenDb.SaveChangesAsync();

            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var minimum = SiteStatsBuilder.MinLearnersPerStudiedDeck;
            for (var i = 0; i < minimum; i++)
                userDb.UserStudyDecks.Add(new Jiten.Core.Data.User.UserStudyDeck { UserId = Learners[i], DeckId = i == 0 ? 10 : 11, Name = "deck" });
            for (var i = 0; i < minimum - 1; i++)
                userDb.UserStudyDecks.Add(new Jiten.Core.Data.User.UserStudyDeck { UserId = Learners[i], DeckId = 20, Name = "deck" });
            await userDb.SaveChangesAsync();
        }

        var studied = (await RunSnapshot()).Studying.Should().ContainSingle().Subject;

        studied.DeckId.Should().Be(10);
        studied.Learners.Should().Be(SiteStatsBuilder.MinLearnersPerStudiedDeck);
    }

    [Fact]
    public async Task Reading_folds_media_types_under_the_learner_floor_into_other()
    {
        await SeedLearners();
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            foreach (var id in Learners)
                userDb.UserAccomplishments.Add(new UserAccomplishment { UserId = id, MediaType = MediaType.Anime, TotalCharacterCount = 1000, CompletedDeckCount = 1, CompletedUnitCount = 12 });
            userDb.UserAccomplishments.Add(new UserAccomplishment { UserId = Learners[0], MediaType = MediaType.Movie, TotalCharacterCount = 500, CompletedDeckCount = 1 });
            await userDb.SaveChangesAsync();
        }

        var stats = await RunSnapshot();

        var anime = stats.Reading.Should().ContainSingle().Subject;
        anime.MediaType.Should().Be(MediaType.Anime);
        anime.UnitsFinished.Should().Be(12L * LearnerCount);
        stats.Totals.TitlesFinished.Should().Be(LearnerCount + 1);
        stats.Totals.CharactersRead.Should().Be(1000L * LearnerCount + 500);
    }

    [Fact]
    public async Task Sections_below_the_learner_floor_stay_empty()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.Users.Add(new User { Id = Learners[0], UserName = Learners[0], CreatedAt = Now.AddDays(-10), UpdatedAt = Now });
            userDb.UserReviewDailies.Add(new UserReviewDaily { UserId = Learners[0], LocalDate = Today, ReviewCount = 5 });
            await userDb.SaveChangesAsync();
        }

        var stats = await RunSnapshot();

        stats.Totals.Learners.Should().Be(1);
        stats.KnownWords.Should().BeNull();
        stats.FrequencyBands.Should().BeEmpty();
        stats.Heatmap.Should().BeNull();
    }
}
