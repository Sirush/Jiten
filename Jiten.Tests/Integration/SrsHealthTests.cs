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

public class SrsHealthTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        // ResetDatabaseAsync does not clear the FSRS tables, so wipe them to avoid card-key collisions.
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Seeds one card and a run of review logs on consecutive days (so the unique (CardId, ReviewDateTime)
    // index never collides and same-day pairs are zero).
    private async Task SeedCard(int wordId, IReadOnlyList<FsrsRating> logs)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var card = new FsrsCard(TestUsers.UserA, wordId, 0, state: FsrsState.Review);
        userDb.FsrsCards.Add(card);
        await userDb.SaveChangesAsync();

        var start = DateTime.UtcNow.AddDays(-logs.Count - 1);
        for (var i = 0; i < logs.Count; i++)
            userDb.FsrsReviewLogs.Add(new FsrsReviewLog(card.CardId, logs[i], start.AddDays(i)));
        await userDb.SaveChangesAsync();
    }

    private static List<FsrsRating> Repeat(FsrsRating rating, int n)
        => Enumerable.Range(0, n).Select(_ => rating).ToList();

    private async Task<FsrsHealthResponse> GetHealth()
    {
        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/srs/settings/health").WithUser(TestUsers.UserA));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<FsrsHealthResponse>())!;
    }

    [Fact]
    public async Task Health_ReportsRatingDistribution()
    {
        var logs = new List<FsrsRating>();
        logs.AddRange(Repeat(FsrsRating.Again, 3));
        logs.AddRange(Repeat(FsrsRating.Hard, 5));
        logs.AddRange(Repeat(FsrsRating.Good, 10));
        logs.AddRange(Repeat(FsrsRating.Easy, 2));
        await SeedCard(1, logs);

        var health = await GetHealth();

        health.TotalReviews.Should().Be(20);
        health.RatingCounts.Should().Equal(3, 5, 10, 2);
    }

    [Fact]
    public async Task Health_FlagsLikelyHardAsFail_WhenHardHighAndAgainAbsent()
    {
        var logs = new List<FsrsRating>();
        logs.AddRange(Repeat(FsrsRating.Hard, 12)); // 24%
        logs.AddRange(Repeat(FsrsRating.Good, 38)); // no Again at all
        await SeedCard(1, logs);

        var health = await GetHealth();

        health.TotalReviews.Should().Be(50);
        health.LikelyHardAsFail.Should().BeTrue();
        health.NeverUsesEasy.Should().BeTrue();
    }

    [Fact]
    public async Task Health_DoesNotFlagHardAsFail_WhenAgainIsUsedNormally()
    {
        var logs = new List<FsrsRating>();
        logs.AddRange(Repeat(FsrsRating.Again, 8));
        logs.AddRange(Repeat(FsrsRating.Hard, 12));
        logs.AddRange(Repeat(FsrsRating.Good, 25));
        logs.AddRange(Repeat(FsrsRating.Easy, 5));
        await SeedCard(1, logs);

        var health = await GetHealth();

        health.LikelyHardAsFail.Should().BeFalse("Again is used at a healthy rate");
        health.NeverUsesHard.Should().BeFalse();
        health.NeverUsesEasy.Should().BeFalse();
    }

    [Fact]
    public async Task RemapHard_RewritesHardToAgain()
    {
        var logs = new List<FsrsRating>();
        logs.AddRange(Repeat(FsrsRating.Hard, 5));
        logs.AddRange(Repeat(FsrsRating.Good, 5));
        await SeedCard(1, logs);

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/srs/settings/remap-hard")
                .WithUser(TestUsers.UserA)
                .WithJsonContent(new { reschedule = false }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("remapped").GetInt32().Should().Be(5);

        var health = await GetHealth();
        health.RatingCounts[1].Should().Be(0, "all Hard reviews were remapped");
        health.RatingCounts[0].Should().Be(5, "the 5 Hard reviews became Again");
        health.RatingCounts[2].Should().Be(5, "Good reviews are untouched");
    }
}
