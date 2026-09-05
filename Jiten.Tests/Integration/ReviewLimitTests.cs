using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
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
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A card introduced today with three learning-step logs, and an older card lapsed and recovered today.</summary>
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
                                    .WithJsonContent(dto))).EnsureSuccessStatusCode();

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
}
