using System.Net;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class BuryUndoTests(JitenWebApplicationFactory factory)
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
        await userDb.UserReviewDailies.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedCard(int wordId, DateTime due)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, 0, state: FsrsState.Review, stability: 30, due: due));
        await userDb.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> SetState(int wordId, string state, DateTime? restoreDue = null)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/set-vocabulary-state")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(new { wordId, readingIndex = 0, state, restoreDue }));

    private async Task<FsrsCard> GetCard(int wordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsCards.AsNoTracking().FirstAsync(c => c.UserId == TestUsers.UserA && c.WordId == wordId);
    }

    [Fact]
    public async Task Burying_PushesTheCardToTheNextMidnight()
    {
        await SeedCard(1, DateTime.UtcNow.AddDays(-3));

        (await SetState(1, "bury-add")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetCard(1)).Due.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task Unburying_RestoresTheDueDateTheClientHandsBack()
    {
        var due = DateTime.UtcNow.AddDays(-3);
        await SeedCard(2, DateTime.UtcNow.AddHours(8));

        (await SetState(2, "bury-remove", due)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetCard(2)).Due.Should().BeCloseTo(due, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Unburying_WithoutADueDate_FallsBackToNow()
    {
        await SeedCard(3, DateTime.UtcNow.AddDays(20));

        (await SetState(3, "bury-remove")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetCard(3)).Due.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Unburying_IgnoresADueDateLaterThanTheCurrentOne()
    {
        await SeedCard(4, DateTime.UtcNow.AddHours(8));

        (await SetState(4, "bury-remove", DateTime.UtcNow.AddYears(5))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await GetCard(4)).Due.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }
}
