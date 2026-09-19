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

public class AgainBuryTests(JitenWebApplicationFactory factory)
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

    private static DateTime Tomorrow => DateTime.UtcNow.Date.AddDays(1);

    private async Task SetThreshold(int threshold, LeechAction leechAction = LeechAction.NotifyOnly)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserFsrsSettings.Add(new UserFsrsSettings
                                    {
                                        UserId = TestUsers.UserA,
                                        SettingsJson = JsonSerializer.Serialize(new StudySettingsDto
                                                                                {
                                                                                    AgainBuryThreshold = threshold,
                                                                                    LeechThreshold = 8,
                                                                                    LeechAction = leechAction
                                                                                })
                                    });
        await userDb.SaveChangesAsync();
    }

    private async Task<long> SeedCard(int wordId, FsrsState state, int lapses = 0)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var card = new FsrsCard(TestUsers.UserA, wordId, 0, state: state, stability: state == FsrsState.Review ? 30 : 1, difficulty: 6,
                                due: DateTime.UtcNow.AddHours(-1), lastReview: DateTime.UtcNow.AddDays(-1))
                   { Lapses = lapses, Step = state == FsrsState.Learning ? 0 : null };
        userDb.FsrsCards.Add(card);
        await userDb.SaveChangesAsync();
        return card.CardId;
    }

    private async Task SeedAgainLogs(long cardId, int count, DateTime at)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        for (var i = 0; i < count; i++)
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(cardId, FsrsRating.Again, at.AddSeconds(i)));
        await userDb.SaveChangesAsync();
    }

    private async Task<FsrsCard> GetCard(int wordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsCards.AsNoTracking().FirstAsync(c => c.UserId == TestUsers.UserA && c.WordId == wordId);
    }

    private async Task<JsonElement> Review(int wordId, FsrsRating rating)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new
                                                                {
                                                                    wordId, readingIndex = 0, rating = (int)rating,
                                                                    clientRequestId = Guid.NewGuid().ToString("N")
                                                                }));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task ThresholdZero_NeverBuries()
    {
        await SetThreshold(0);
        await SeedCard(1, FsrsState.Review);

        for (var i = 0; i < 5; i++)
            (await Review(1, FsrsRating.Again)).GetProperty("autoBuried").GetBoolean().Should().BeFalse();

        (await GetCard(1)).Due.Should().BeBefore(Tomorrow);
    }

    [Fact]
    public async Task TheNthAgainOfTheDay_BuriesUntilTomorrow()
    {
        await SetThreshold(3);
        await SeedCard(2, FsrsState.Review);

        (await Review(2, FsrsRating.Again)).GetProperty("autoBuried").GetBoolean().Should().BeFalse();
        (await Review(2, FsrsRating.Again)).GetProperty("autoBuried").GetBoolean().Should().BeFalse();
        (await GetCard(2)).Due.Should().BeBefore(Tomorrow);

        var third = await Review(2, FsrsRating.Again);
        third.GetProperty("autoBuried").GetBoolean().Should().BeTrue();
        third.GetProperty("nextDue").GetDateTime().ToUniversalTime().Should().BeAfter(Tomorrow);
        (await GetCard(2)).Due.Should().BeAfter(Tomorrow);
    }

    [Fact]
    public async Task YesterdaysAgains_DoNotCount()
    {
        await SetThreshold(3);
        var cardId = await SeedCard(3, FsrsState.Review);
        await SeedAgainLogs(cardId, 5, DateTime.UtcNow.Date.AddHours(-2));

        (await Review(3, FsrsRating.Again)).GetProperty("autoBuried").GetBoolean().Should().BeFalse();
        (await GetCard(3)).Due.Should().BeBefore(Tomorrow);
    }

    [Fact]
    public async Task TodaysEarlierAgains_CountAcrossSessions()
    {
        await SetThreshold(3);
        var cardId = await SeedCard(4, FsrsState.Review);
        await SeedAgainLogs(cardId, 2, DateTime.UtcNow.AddMinutes(-30));

        (await Review(4, FsrsRating.Again)).GetProperty("autoBuried").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ALearningCard_BuriesWithoutEverLapsing()
    {
        await SetThreshold(2);
        await SeedCard(5, FsrsState.Learning);

        await Review(5, FsrsRating.Again);
        (await Review(5, FsrsRating.Again)).GetProperty("autoBuried").GetBoolean().Should().BeTrue();

        var card = await GetCard(5);
        card.Lapses.Should().Be(0);
        card.Due.Should().BeAfter(Tomorrow);
    }

    [Fact]
    public async Task ANonAgainRating_DoesNotBury()
    {
        await SetThreshold(1);
        await SeedCard(6, FsrsState.Review);

        (await Review(6, FsrsRating.Good)).GetProperty("autoBuried").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task LeechSuspension_WinsOverBury()
    {
        await SetThreshold(1, LeechAction.Suspend);
        await SeedCard(7, FsrsState.Review, lapses: 7);

        var result = await Review(7, FsrsRating.Again);
        result.GetProperty("leechSuspended").GetBoolean().Should().BeTrue();
        result.GetProperty("autoBuried").GetBoolean().Should().BeFalse();
        (await GetCard(7)).State.Should().Be(FsrsState.Suspended);
    }

    [Fact]
    public async Task TheBuryingReview_IsLoggedAndRolledUpOnce()
    {
        await SetThreshold(2);
        var cardId = await SeedCard(8, FsrsState.Review);

        await Review(8, FsrsRating.Again);
        await Review(8, FsrsRating.Again);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.FsrsReviewLogs.CountAsync(l => l.CardId == cardId)).Should().Be(2);
        var daily = await userDb.UserReviewDailies.SingleAsync(d => d.UserId == TestUsers.UserA);
        daily.ReviewCount.Should().Be(2);
        daily.CorrectCount.Should().Be(0);
    }

    [Fact]
    public async Task Undo_RestoresTheScheduledDue()
    {
        await SetThreshold(2);
        await SeedCard(9, FsrsState.Review);

        await Review(9, FsrsRating.Again);
        await Review(9, FsrsRating.Again);
        (await GetCard(9)).Due.Should().BeAfter(Tomorrow);

        var undo = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/undo-review")
                                           .WithUser(TestUsers.UserA)
                                           .WithJsonContent(new { wordId = 9, readingIndex = 0 }));
        undo.StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetCard(9)).Due.Should().BeBefore(Tomorrow);
    }

    [Fact]
    public async Task BatchReview_AppliesTheSameRule()
    {
        await SetThreshold(2);
        var cardId = await SeedCard(10, FsrsState.Review);
        await SeedCard(11, FsrsState.Review);
        await SeedAgainLogs(cardId, 1, DateTime.UtcNow.AddMinutes(-10));

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/batch-review")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new
                                                                {
                                                                    reviews = new[]
                                                                    {
                                                                        new { wordId = 10, readingIndex = 0, rating = 1 },
                                                                        new { wordId = 11, readingIndex = 0, rating = 1 },
                                                                    }
                                                                }));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("autoBuried").EnumerateArray().Select(e => e.GetInt32()).Should().Equal(10);
        (await GetCard(10)).Due.Should().BeAfter(Tomorrow);
        (await GetCard(11)).Due.Should().BeBefore(Tomorrow);
    }

    [Fact]
    public async Task StudySettings_ClampTheThreshold()
    {
        var put = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
                                          .WithUser(TestUsers.UserA)
                                          .WithJsonContent(new { againBuryThreshold = 500 }));
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/srs/study-settings").WithUser(TestUsers.UserA));
        var settings = await get.Content.ReadFromJsonAsync<JsonElement>();
        settings.GetProperty("againBuryThreshold").GetInt32().Should().Be(99);
    }
}
