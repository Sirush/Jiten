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

public class LeechSuspensionTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const int Threshold = 8;

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SetLeechAction(LeechAction action)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserFsrsSettings.Add(new UserFsrsSettings
                                    {
                                        UserId = TestUsers.UserA,
                                        SettingsJson = JsonSerializer.Serialize(new StudySettingsDto
                                                                                {
                                                                                    LeechThreshold = Threshold,
                                                                                    LeechAction = action
                                                                                })
                                    });
        await userDb.SaveChangesAsync();
    }

    private async Task SeedCard(int wordId, int lapses, FsrsState state, double stability)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, 0, state: state, stability: stability, difficulty: 8,
                                          due: DateTime.UtcNow.AddDays(-1), lastReview: DateTime.UtcNow.AddDays(-3))
                            { Lapses = lapses, Step = state == FsrsState.Relearning ? 0 : null });
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
    public async Task ReachingTheThreshold_Suspends()
    {
        await SetLeechAction(LeechAction.Suspend);
        await SeedCard(1, lapses: Threshold - 1, FsrsState.Review, stability: 30);

        var body = await Review(1, FsrsRating.Again);

        body.GetProperty("leechSuspended").GetBoolean().Should().BeTrue();
        (await GetCard(1)).State.Should().Be(FsrsState.Suspended);
    }

    /// <summary>The reported bug: lapses past the threshold but off the half-threshold notify step.</summary>
    [Fact]
    public async Task FailingAnAlreadyFlaggedLeech_Suspends_EvenOffTheNotifyStep()
    {
        await SetLeechAction(LeechAction.Suspend);
        await SeedCard(1, lapses: Threshold + 1, FsrsState.Review, stability: 4);

        var body = await Review(1, FsrsRating.Again);

        body.GetProperty("leechSuspended").GetBoolean().Should().BeTrue();
        (await GetCard(1)).State.Should().Be(FsrsState.Suspended);
    }

    /// <summary>A relearning card takes no lapse, so only the flag itself can trigger the suspension.</summary>
    [Fact]
    public async Task FailingALeechInRelearning_Suspends_WithoutCountingALapse()
    {
        await SetLeechAction(LeechAction.Suspend);
        await SeedCard(1, lapses: Threshold, FsrsState.Relearning, stability: 3);

        var body = await Review(1, FsrsRating.Again);

        body.GetProperty("leechSuspended").GetBoolean().Should().BeTrue();
        var card = await GetCard(1);
        card.State.Should().Be(FsrsState.Suspended);
        card.Lapses.Should().Be(Threshold);
    }

    [Fact]
    public async Task AnsweringALeechCorrectly_LeavesItInTheQueue()
    {
        await SetLeechAction(LeechAction.Suspend);
        await SeedCard(1, lapses: Threshold + 1, FsrsState.Review, stability: 4);

        var body = await Review(1, FsrsRating.Good);

        body.GetProperty("leechSuspended").GetBoolean().Should().BeFalse();
        (await GetCard(1)).State.Should().Be(FsrsState.Review);
    }

    [Fact]
    public async Task NotifyOnly_FlagsWithoutSuspending()
    {
        await SetLeechAction(LeechAction.NotifyOnly);
        await SeedCard(1, lapses: Threshold - 1, FsrsState.Review, stability: 30);

        var body = await Review(1, FsrsRating.Again);

        body.GetProperty("leechDetected").GetBoolean().Should().BeTrue();
        body.GetProperty("leechSuspended").GetBoolean().Should().BeFalse();
        (await GetCard(1)).State.Should().Be(FsrsState.Relearning);
    }

    /// <summary>Between notify steps a long-standing leech must not nag on every failure.</summary>
    [Fact]
    public async Task NotifyOnly_StaysQuietBetweenSteps()
    {
        await SetLeechAction(LeechAction.NotifyOnly);
        await SeedCard(1, lapses: Threshold + 1, FsrsState.Review, stability: 4);

        var body = await Review(1, FsrsRating.Again);

        body.GetProperty("leechDetected").GetBoolean().Should().BeFalse();
        body.GetProperty("isLeech").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task BatchReview_Suspends_AnAlreadyFlaggedLeech()
    {
        await SetLeechAction(LeechAction.Suspend);
        await SeedCard(1, lapses: Threshold + 1, FsrsState.Review, stability: 4);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/batch-review")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { reviews = new[] { new { wordId = 1, readingIndex = 0, rating = 1 } } }));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("leechSuspended").EnumerateArray().Select(e => e.GetInt32()).Should().Contain(1);
        (await GetCard(1)).State.Should().Be(FsrsState.Suspended);
    }
}
