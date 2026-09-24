using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ReviewLimitTests(JitenWebApplicationFactory factory)
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
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();
        await userDb.UserReviewDailies.ExecuteDeleteAsync();
        // "Today" is the user's local day, so pin it to local noon or the minutes-ago seeds straddle midnight near 00:00 UTC.
        userDb.UserFsrsSettings.Add(new UserFsrsSettings
                                    {
                                        UserId = TestUsers.UserA,
                                        SettingsJson = JsonSerializer.Serialize(new StudySettingsDto { Timezone = _middayZone })
                                    });
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private readonly string _middayZone = TestZones.WithLocalHour(DateTime.UtcNow, 12);

    /// <summary>A card introduced today with three learning-step logs, and a card reviewed ten days ago, lapsed and recovered today.</summary>
    private async Task SeedTodaysActivity()
    {
        var now = DateTime.UtcNow;
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var introducedToday = new FsrsCard(TestUsers.UserA, 1, 0, state: FsrsState.Learning, step: 0, stability: 1,
                                           difficulty: 5, due: now.AddMinutes(10), lastReview: now.AddMinutes(-1));
        foreach (var minutesAgo in new[] { 30, 15, 1 })
            introducedToday.ReviewLogs.Add(new FsrsReviewLog { Rating = FsrsRating.Again, ReviewDateTime = now.AddMinutes(-minutesAgo) });

        var lapsedToday = new FsrsCard(TestUsers.UserA, 2, 0, state: FsrsState.Review, stability: 5, difficulty: 6,
                                       due: now.AddDays(3), lastReview: now.AddMinutes(-5))
                          { CreatedAt = now.AddDays(-40) };
        lapsedToday.ReviewLogs.Add(new FsrsReviewLog { Rating = FsrsRating.Good, ReviewDateTime = now.AddDays(-10) });
        lapsedToday.ReviewLogs.Add(new FsrsReviewLog { Rating = FsrsRating.Again, ReviewDateTime = now.AddMinutes(-20) });
        lapsedToday.ReviewLogs.Add(new FsrsReviewLog { Rating = FsrsRating.Good, ReviewDateTime = now.AddMinutes(-5) });

        userDb.FsrsCards.AddRange(introducedToday, lapsedToday);
        await userDb.SaveChangesAsync();
    }

    private async Task SeedDueOldCard(int wordId)
    {
        var now = DateTime.UtcNow;
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, 0, state: FsrsState.Review, stability: 20, difficulty: 5,
                                          due: now.AddHours(-2), lastReview: now.AddDays(-20)) { CreatedAt = now.AddDays(-60) });
        await userDb.SaveChangesAsync();
    }

    private async Task PutSettings(StudySettingsDto dto)
        => (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
                                    .WithUser(TestUsers.UserA)
                                    .WithJsonContent(WithMiddayZone(dto)))).EnsureSuccessStatusCode();

    private StudySettingsDto WithMiddayZone(StudySettingsDto dto)
    {
        dto.Timezone ??= _middayZone;
        return dto;
    }


    private async Task<JsonElement> Get(string path)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path).WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task ReviewsToday_ExcludesLearningStepsOfCardsIntroducedToday()
    {
        await SeedTodaysActivity();

        var batch = await Get("/api/srs/study-batch?limit=10");
        batch.GetProperty("reviewsToday").GetInt32().Should().Be(2, "only the lapsed card's two reviews count");
        batch.GetProperty("newCardsToday").GetInt32().Should().Be(1);

        var summary = await Get("/api/srs/due-summary");
        summary.GetProperty("reviewsToday").GetInt32().Should().Be(2);
        summary.GetProperty("newCardsToday").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ReviewsToday_WithoutFailedReviews_CountsUniqueOldCardsOnly()
    {
        await PutSettings(new StudySettingsDto { CountFailedReviews = false });
        await SeedTodaysActivity();

        (await Get("/api/srs/study-batch?limit=10")).GetProperty("reviewsToday").GetInt32().Should().Be(1);
        (await Get("/api/srs/due-summary")).GetProperty("reviewsToday").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ACardWithOldReviews_ButCreatedToday_CountsAsAReview()
    {
        // Archive restore recreates the card today with its old logs; it must not count as new.
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var restored = new FsrsCard(TestUsers.UserA, 7, 0, state: FsrsState.Review, stability: 8, difficulty: 5,
                                        due: now.AddDays(2), lastReview: now.AddMinutes(-3));
            restored.ReviewLogs.Add(new FsrsReviewLog { Rating = FsrsRating.Good, ReviewDateTime = now.AddDays(-15) });
            restored.ReviewLogs.Add(new FsrsReviewLog { Rating = FsrsRating.Good, ReviewDateTime = now.AddMinutes(-3) });
            userDb.FsrsCards.Add(restored);
            await userDb.SaveChangesAsync();
        }

        var summary = await Get("/api/srs/due-summary");
        summary.GetProperty("reviewsToday").GetInt32().Should().Be(1);
        summary.GetProperty("newCardsToday").GetInt32().Should().Be(0);

        var batch = await Get("/api/srs/study-batch?limit=10");
        batch.GetProperty("reviewsToday").GetInt32().Should().Be(1);
        batch.GetProperty("newCardsToday").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task ACardCreatedLongAgo_FirstReviewedToday_CountsAsNew()
    {
        // Reset-schedule leaves CreatedAt old; only the review history decides.
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var card = new FsrsCard(TestUsers.UserA, 8, 0, state: FsrsState.Learning, step: 0, stability: 1, difficulty: 5,
                                    due: now.AddMinutes(10), lastReview: now.AddMinutes(-1)) { CreatedAt = now.AddDays(-30) };
            card.ReviewLogs.Add(new FsrsReviewLog { Rating = FsrsRating.Good, ReviewDateTime = now.AddMinutes(-1) });
            userDb.FsrsCards.Add(card);
            await userDb.SaveChangesAsync();
        }

        var summary = await Get("/api/srs/due-summary");
        summary.GetProperty("reviewsToday").GetInt32().Should().Be(0);
        summary.GetProperty("newCardsToday").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task NewCardLearningSteps_DoNotConsumeTheReviewBudget()
    {
        // Five logs today in total; a cap of 3 would already be exhausted if new-card steps counted.
        await PutSettings(new StudySettingsDto { MaxReviewsPerDay = 3 });
        await SeedTodaysActivity();
        await SeedDueOldCard(3);

        var summary = await Get("/api/srs/due-summary");
        summary.GetProperty("reviewBudgetLeft").GetInt32().Should().Be(1);

        var batch = await Get("/api/srs/study-batch?limit=10");
        var wordIds = batch.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("wordId").GetInt32()).ToList();
        wordIds.Should().Contain(3);
    }

    private async Task<List<int>> BatchWordIds(string query)
        => (await Get($"/api/srs/study-batch?limit=10{query}")).GetProperty("cards").EnumerateArray()
                                                               .Select(c => c.GetProperty("wordId").GetInt32()).ToList();

    [Fact]
    public async Task ReviewAhead_ServesCardsPastAnExhaustedDailyCap()
    {
        await PutSettings(new StudySettingsDto { MaxReviewsPerDay = 2 });
        await SeedTodaysActivity();
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, 4, 0, state: FsrsState.Review, stability: 10, difficulty: 5,
                                              due: now.AddHours(12), lastReview: now.AddDays(-10)) { CreatedAt = now.AddDays(-30) });
            await userDb.SaveChangesAsync();
        }

        (await Get("/api/srs/due-summary")).GetProperty("reviewBudgetLeft").GetInt32().Should().Be(0);
        (await BatchWordIds("")).Should().BeEmpty();
        var count = (await Get("/api/srs/study-more-count?mode=ahead&aheadMinutes=1440")).GetProperty("count").GetInt32();
        var ahead = await BatchWordIds("&aheadMinutes=1440");
        ahead.Should().Contain(4);
        ahead.Should().HaveCount(count);
    }

    [Fact]
    public async Task RecentMistakes_ServesCardsPastAnExhaustedDailyCap_IncludingOnesRecoveredToday()
    {
        await PutSettings(new StudySettingsDto { MaxReviewsPerDay = 2 });
        await SeedTodaysActivity();
        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var recovered = new FsrsCard(TestUsers.UserA, 5, 0, state: FsrsState.Review, stability: 3, difficulty: 7,
                                         due: now.AddDays(2), lastReview: now.AddDays(-1)) { CreatedAt = now.AddDays(-30) };
            recovered.ReviewLogs.Add(new FsrsReviewLog { Rating = FsrsRating.Good, ReviewDateTime = now.AddDays(-12) });
            recovered.ReviewLogs.Add(new FsrsReviewLog { Rating = FsrsRating.Again, ReviewDateTime = now.AddDays(-2) });
            recovered.ReviewLogs.Add(new FsrsReviewLog { Rating = FsrsRating.Good, ReviewDateTime = now.AddDays(-1) });
            userDb.FsrsCards.Add(recovered);
            await userDb.SaveChangesAsync();
        }

        // Word 2 lapsed and recovered today; word 1 is still in its learning steps, so only it is left out.
        (await Get("/api/srs/study-more-count?mode=mistakes&mistakeDays=3")).GetProperty("count").GetInt32().Should().Be(2);
        var first = await Get("/api/srs/study-batch?limit=10&mistakeDays=3");
        first.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("wordId").GetInt32()).Should().BeEquivalentTo([2, 5]);

        var review = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
                                             .WithUser(TestUsers.UserA)
                                             .WithJsonContent(new { wordId = 5, readingIndex = 0, rating = FsrsRating.Good }));
        review.EnsureSuccessStatusCode();

        var anchor = Uri.EscapeDataString(first.GetProperty("serverTime").GetString()!);
        (await BatchWordIds($"&mistakeDays=3&reviewedBefore={anchor}")).Should().Equal(2);
    }
}
