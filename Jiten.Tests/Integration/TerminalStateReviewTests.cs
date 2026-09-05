using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class TerminalStateReviewTests(JitenWebApplicationFactory factory)
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
        await userDb.UserReviewDailies.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedCard(int wordId, FsrsState state)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, 0, state: state, stability: 10));
        await userDb.SaveChangesAsync();
    }

    private async Task<FsrsCard> GetCard(int wordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsCards.AsNoTracking().Include(c => c.ReviewLogs)
                           .FirstAsync(c => c.UserId == TestUsers.UserA && c.WordId == wordId);
    }

    [Theory]
    [InlineData(FsrsState.Suspended)]
    [InlineData(FsrsState.Mastered)]
    [InlineData(FsrsState.Blacklisted)]
    public async Task Review_OfATerminalCard_IsRejectedAndLeavesNoTrace(FsrsState state)
    {
        await SeedCard(1, state);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { wordId = 1, readingIndex = 0, rating = 3 }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var card = await GetCard(1);
        card.State.Should().Be(state);
        card.ReviewLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Review_OfANewStateCard_SchedulesItAsLearning()
    {
        await SeedCard(2, FsrsState.New);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { wordId = 2, readingIndex = 0, rating = 3 }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var card = await GetCard(2);
        card.State.Should().NotBe(FsrsState.New);
        card.ReviewLogs.Should().HaveCount(1);
    }

    [Fact]
    public async Task BatchReview_SkipsTerminalCards_AndReportsThem()
    {
        await SeedCard(3, FsrsState.Suspended);
        await SeedCard(4, FsrsState.Review);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/batch-review")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new
                                               {
                                                   reviews = new[]
                                                   {
                                                       new { wordId = 3, readingIndex = 0, rating = 3 },
                                                       new { wordId = 4, readingIndex = 0, rating = 3 },
                                                       new { wordId = 5, readingIndex = 0, rating = 3 },
                                                   }
                                               }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("processed").GetInt32().Should().Be(2);
        body.GetProperty("results").GetArrayLength().Should().Be(2);
        var skipped = body.GetProperty("skipped");
        skipped.GetArrayLength().Should().Be(1);
        skipped[0].GetProperty("wordId").GetInt32().Should().Be(3);
        skipped[0].GetProperty("state").GetInt32().Should().Be((int)FsrsState.Suspended);

        (await GetCard(3)).ReviewLogs.Should().BeEmpty();
        (await GetCard(4)).ReviewLogs.Should().HaveCount(1);
        (await GetCard(5)).ReviewLogs.Should().HaveCount(1);
    }
}
