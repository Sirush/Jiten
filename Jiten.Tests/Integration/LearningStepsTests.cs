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

public class LearningStepsTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    // ResetDatabaseAsync keeps the FSRS tables, so settings and cards would leak between tests here.
    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.Where(l => l.Card.UserId == TestUsers.UserA).ExecuteDeleteAsync();
        await userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA).ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.Where(s => s.UserId == TestUsers.UserA).ExecuteDeleteAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Defaults_AreSingleTenMinuteSteps()
    {
        var settings = await GetSettings();
        settings.LearningSteps.Should().Equal(10);
        settings.RelearningSteps.Should().Equal(10);
        settings.LearnAheadMinutes.Should().Be(20);
    }

    [Fact]
    public async Task Steps_RoundTrip_AndStaleClientLeavesThemUntouched()
    {
        var put = await PutSettings(new StudySettingsDto { LearningSteps = [5, 30], RelearningSteps = [], LearnAheadMinutes = 45 });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var settings = await GetSettings();
        settings.LearningSteps.Should().Equal(5, 30);
        settings.RelearningSteps.Should().BeEmpty();
        settings.LearnAheadMinutes.Should().Be(45);

        var stale = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
                                            .WithUser(TestUsers.UserA)
                                            .WithJsonContent(new { newCardsPerDay = 15 }));
        stale.StatusCode.Should().Be(HttpStatusCode.OK);

        settings = await GetSettings();
        settings.LearningSteps.Should().Equal(5, 30);
        settings.RelearningSteps.Should().BeEmpty();
        settings.NewCardsPerDay.Should().Be(15);
    }

    [Theory]
    [InlineData(new[] { 1440 })]
    [InlineData(new[] { 30, 10 })]
    [InlineData(new[] { 1, 2, 3, 4, 5 })]
    public async Task InvalidSteps_AreRejected(int[] steps)
    {
        var put = await PutSettings(new StudySettingsDto { RelearningSteps = steps });
        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var settings = await GetSettings();
        settings.RelearningSteps.Should().Equal(10);
    }

    [Fact]
    public async Task Review_UsesTheUsersRelearningStep_AndRecomputeKeepsIt()
    {
        (await PutSettings(new StudySettingsDto { RelearningSteps = [30] })).EnsureSuccessStatusCode();

        // Good graduates straight to Review under the single default learning step; the recompute below
        // replays from logs, so the history has to come from real reviews rather than a seeded card.
        var graduate = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { wordId = 1, readingIndex = 0, rating = 3 }));
        graduate.StatusCode.Should().Be(HttpStatusCode.OK);
        (await graduate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("newState").GetInt32().Should().Be((int)FsrsState.Review);
        await Task.Delay(1100); // per-word review debounce

        var review = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
                                             .WithUser(TestUsers.UserA)
                                             .WithJsonContent(new { wordId = 1, readingIndex = 0, rating = 1 }));
        review.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await review.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("newState").GetInt32().Should().Be((int)FsrsState.Relearning);
        var nextDue = body.GetProperty("nextDue").GetDateTime().ToUniversalTime();
        (nextDue - DateTime.UtcNow).TotalMinutes.Should().BeInRange(28, 31);

        var recompute = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/settings/recompute-batch")
                                                .WithUser(TestUsers.UserA));
        recompute.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var card = await userDb.FsrsCards.SingleAsync(c => c.UserId == TestUsers.UserA && c.WordId == 1);
            card.State.Should().Be(FsrsState.Relearning);
            (card.Due - card.LastReview!.Value).TotalMinutes.Should().BeApproximately(30, 0.5);
        }
    }

    [Fact]
    public async Task EmptyLearningSteps_GraduateOnTheFirstRating()
    {
        (await PutSettings(new StudySettingsDto { LearningSteps = [] })).EnsureSuccessStatusCode();

        var review = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
                                             .WithUser(TestUsers.UserA)
                                             .WithJsonContent(new { wordId = 1, readingIndex = 0, rating = 3 }));
        review.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await review.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("newState").GetInt32().Should().Be((int)FsrsState.Review);
        var nextDue = body.GetProperty("nextDue").GetDateTime().ToUniversalTime();
        (nextDue - DateTime.UtcNow).TotalDays.Should().BeGreaterThanOrEqualTo(0.9);
    }

    [Theory]
    [InlineData(20, true)]
    [InlineData(5, false)]
    public async Task LearnAhead_ServesALearningCardDueInsideTheWindow(int learnAheadMinutes, bool served)
    {
        (await PutSettings(new StudySettingsDto { LearnAheadMinutes = learnAheadMinutes })).EnsureSuccessStatusCode();

        var now = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, 1, 0, state: FsrsState.Learning, step: 0,
                                              stability: 1, difficulty: 5, due: now.AddMinutes(8), lastReview: now.AddMinutes(-2)));
            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, 2, 0, state: FsrsState.Review,
                                              stability: 10, difficulty: 5, due: now.AddMinutes(8), lastReview: now.AddDays(-10)));
            await userDb.SaveChangesAsync();
        }

        var batch = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-batch?limit=10").WithUser(TestUsers.UserA));
        batch.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await batch.Content.ReadFromJsonAsync<JsonElement>();
        var wordIds = body.GetProperty("cards").EnumerateArray().Select(c => c.GetProperty("wordId").GetInt32()).ToList();
        wordIds.Contains(1).Should().Be(served);
        wordIds.Should().NotContain(2, "a review card is never served ahead of its due time");

        var summary = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/due-summary").WithUser(TestUsers.UserA));
        summary.StatusCode.Should().Be(HttpStatusCode.OK);
        var due = (await summary.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reviewsDue").GetInt32();
        due.Should().Be(served ? 1 : 0);
    }

    private async Task<HttpResponseMessage> PutSettings(StudySettingsDto dto)
        => await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
                                   .WithUser(TestUsers.UserA)
                                   .WithJsonContent(dto));

    private async Task<StudySettingsDto> GetSettings()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-settings").WithUser(TestUsers.UserA));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<StudySettingsDto>())!;
    }
}
