using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jiten.Parser.Tests.Integration;

public class LapseCountingTests(JitenWebApplicationFactory factory)
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
        await userDb.UserFsrsSettings.Where(s => s.UserId == TestUsers.UserA).ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<long> SeedCard(int wordId, int lapses, params (double HoursAgo, FsrsRating Rating)[] history)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var card = new FsrsCard(TestUsers.UserA, wordId, 0, state: FsrsState.Review, stability: 4, difficulty: 6,
                                due: DateTime.UtcNow, lastReview: DateTime.UtcNow.AddHours(history.Length == 0 ? -72 : -history.Min(h => h.HoursAgo)))
                   { Lapses = lapses };
        userDb.FsrsCards.Add(card);
        await userDb.SaveChangesAsync();

        foreach (var (hoursAgo, rating) in history)
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(card.CardId, rating, DateTime.UtcNow.AddHours(-hoursAgo)));
        await userDb.SaveChangesAsync();
        return card.CardId;
    }

    private async Task<int> Lapses(int wordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsCards.Where(c => c.UserId == TestUsers.UserA && c.WordId == wordId).Select(c => c.Lapses).FirstAsync();
    }

    private async Task WithoutRelearningSteps()
        => (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
                                    .WithUser(TestUsers.UserA)
                                    .WithJsonContent(new StudySettingsDto { RelearningSteps = [] })))
            .EnsureSuccessStatusCode();

    private async Task<JsonElement> Review(int wordId, FsrsRating rating)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { wordId, readingIndex = 0, rating = (int)rating, clientRequestId = Guid.NewGuid().ToString() }));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task BatchReview(int wordId, FsrsRating rating)
        => (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/batch-review")
                                    .WithUser(TestUsers.UserA)
                                    .WithJsonContent(new
                                    {
                                        reviews = new[] { new { wordId, readingIndex = 0, rating = (int)rating } },
                                        clientRequestId = Guid.NewGuid().ToString()
                                    })))
            .EnsureSuccessStatusCode();

    [Fact]
    public async Task Review_RepeatAgainsWithoutRelearningSteps_CountOneLapse()
    {
        await WithoutRelearningSteps();
        await SeedCard(1, lapses: 0, (24, FsrsRating.Good));

        (await Review(1, FsrsRating.Again)).GetProperty("newState").GetInt32().Should().Be((int)FsrsState.Review);
        await Review(1, FsrsRating.Again);
        await Review(1, FsrsRating.Again);

        (await Lapses(1)).Should().Be(1);
    }

    [Fact]
    public async Task Review_AgainAfterAPass_IsANewLapse()
    {
        await WithoutRelearningSteps();
        await SeedCard(1, lapses: 0, (24, FsrsRating.Good));

        await Review(1, FsrsRating.Again);
        await Review(1, FsrsRating.Good);
        await Review(1, FsrsRating.Again);

        (await Lapses(1)).Should().Be(2);
    }

    [Fact]
    public async Task Review_ReviewCardWithoutHistory_Lapses()
    {
        await SeedCard(1, lapses: 0);

        await Review(1, FsrsRating.Again);

        (await Lapses(1)).Should().Be(1);
    }

    [Fact]
    public async Task BatchReview_RepeatAgainsWithoutRelearningSteps_CountOneLapse()
    {
        await WithoutRelearningSteps();
        await SeedCard(1, lapses: 0, (24, FsrsRating.Good));

        await BatchReview(1, FsrsRating.Again);
        await BatchReview(1, FsrsRating.Again);

        (await Lapses(1)).Should().Be(1);
    }

    [Fact]
    public async Task Undo_WithoutRelearningSteps_KeepsRepeatAgainsOutOfTheCount()
    {
        await WithoutRelearningSteps();
        await SeedCard(1, lapses: 1, (30, FsrsRating.Good), (26, FsrsRating.Again), (25.9, FsrsRating.Again), (25.8, FsrsRating.Again),
                       (25.7, FsrsRating.Good));

        await Review(1, FsrsRating.Good);
        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/undo-review")
                                 .WithUser(TestUsers.UserA)
                                 .WithJsonContent(new { wordId = 1, readingIndex = 0 })))
            .EnsureSuccessStatusCode();

        (await Lapses(1)).Should().Be(1);
    }

    [Fact]
    public async Task RecountJob_LowersInflatedCounts_AndKeepsClearedOnes()
    {
        var againsAfterAPass = new (double, FsrsRating)[]
        {
            (50, FsrsRating.Good), (48, FsrsRating.Again), (47.9, FsrsRating.Again), (47.8, FsrsRating.Good),
            (24, FsrsRating.Again), (23.9, FsrsRating.Again), (23.8, FsrsRating.Good)
        };
        await SeedCard(1, lapses: 4, againsAfterAPass);
        await SeedCard(2, lapses: 1, againsAfterAPass);
        await SeedCard(3, lapses: 0, againsAfterAPass);

        using (var scope = factory.Services.CreateScope())
        {
            var job = new LapseRecountJob(scope.ServiceProvider.GetRequiredService<IDbContextFactory<UserDbContext>>(),
                                          NullLogger<LapseRecountJob>.Instance);
            await job.RecountAll();
        }

        (await Lapses(1)).Should().Be(2);
        (await Lapses(2)).Should().Be(1);
        (await Lapses(3)).Should().Be(0);
    }
}
