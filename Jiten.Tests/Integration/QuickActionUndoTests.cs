using System.Net;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class QuickActionUndoTests(JitenWebApplicationFactory factory)
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
        await userDb.FsrsCardArchives.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> SetState(int wordId, string state)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/set-vocabulary-state")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(new { wordId, readingIndex = 0, state }));

    private Task<HttpResponseMessage> SetStateBulk(IEnumerable<int> wordIds, string state)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/set-vocabulary-state-bulk")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(new
                             {
                                 state,
                                 items = wordIds.Select(w => new { wordId = w, readingIndex = 0 }).ToList()
                             }));

    private async Task SeedReviewedCard(int wordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var card = new FsrsCard(TestUsers.UserA, wordId, 0, state: FsrsState.Review, stability: 12,
                                due: DateTime.UtcNow.AddDays(3), lastReview: DateTime.UtcNow.AddDays(-9));
        card.ReviewLogs.Add(new FsrsReviewLog
                            {
                                Rating = FsrsRating.Good,
                                ReviewDateTime = DateTime.UtcNow.AddDays(-9)
                            });
        userDb.FsrsCards.Add(card);
        await userDb.SaveChangesAsync();
    }

    private async Task<FsrsCard?> FindCard(int wordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsCards.AsNoTracking()
                           .FirstOrDefaultAsync(c => c.UserId == TestUsers.UserA && c.WordId == wordId);
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("blacklist")]
    [InlineData("neverForget")]
    public async Task UndoingAQuickAction_OnAnUnseenWord_LeavesNoCard(string action)
    {
        (await SetState(1, $"{action}-add")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await FindCard(1)).Should().NotBeNull();

        (await SetState(1, $"{action}-remove")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await FindCard(1)).Should().BeNull();
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("blacklist")]
    [InlineData("neverForget")]
    public async Task UndoingAQuickAction_OnAReviewedWord_RestoresTheReviewCard(string action)
    {
        await SeedReviewedCard(2);

        (await SetState(2, $"{action}-add")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SetState(2, $"{action}-remove")).StatusCode.Should().Be(HttpStatusCode.OK);

        var card = await FindCard(2);
        card.Should().NotBeNull();
        card!.State.Should().Be(FsrsState.Review);
        card.Stability.Should().Be(12);
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("blacklist")]
    [InlineData("neverForget")]
    public async Task BulkUndo_DeletesUnseenCards_AndKeepsReviewedOnes(string action)
    {
        await SeedReviewedCard(3);

        (await SetStateBulk([3, 4, 5], $"{action}-add")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SetStateBulk([3, 4, 5], $"{action}-remove")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await FindCard(3))!.State.Should().Be(FsrsState.Review);
        (await FindCard(4)).Should().BeNull();
        (await FindCard(5)).Should().BeNull();
    }
}
