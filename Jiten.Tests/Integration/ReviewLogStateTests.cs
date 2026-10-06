using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ReviewLogStateTests(JitenWebApplicationFactory factory)
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

    private async Task<List<FsrsState?>> LoggedStates(int wordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsReviewLogs.Where(l => l.Card.UserId == TestUsers.UserA && l.Card.WordId == wordId)
                           .OrderBy(l => l.ReviewDateTime)
                           .Select(l => l.State)
                           .ToListAsync();
    }

    private async Task Review(int wordId, FsrsRating rating)
        => (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
                                    .WithUser(TestUsers.UserA)
                                    .WithJsonContent(new { wordId, readingIndex = 0, rating = (int)rating, clientRequestId = Guid.NewGuid().ToString() })))
            .EnsureSuccessStatusCode();

    [Fact]
    public async Task Review_RecordsTheStateTheCardWasAnsweredIn()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, 1, 0, state: FsrsState.Review, stability: 4, difficulty: 6,
                                              due: DateTime.UtcNow, lastReview: DateTime.UtcNow.AddDays(-3)));
            await userDb.SaveChangesAsync();
        }

        await Review(1, FsrsRating.Again);
        await Review(1, FsrsRating.Good);

        (await LoggedStates(1)).Should().Equal(FsrsState.Review, FsrsState.Relearning);
    }

    [Fact]
    public async Task BatchReview_NewCard_RecordsLearning()
    {
        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/batch-review")
                                 .WithUser(TestUsers.UserA)
                                 .WithJsonContent(new
                                 {
                                     reviews = new[] { new { wordId = 2, readingIndex = 0, rating = (int)FsrsRating.Good } },
                                     clientRequestId = Guid.NewGuid().ToString()
                                 })))
            .EnsureSuccessStatusCode();

        (await LoggedStates(2)).Should().Equal(FsrsState.Learning);
    }
}
