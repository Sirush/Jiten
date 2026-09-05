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

public class LapseClearingTests(JitenWebApplicationFactory factory)
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
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedLeech(int wordId, int lapses = 9)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, 0, state: FsrsState.Review, stability: 4, difficulty: 8,
                                          due: DateTime.UtcNow.AddDays(1), lastReview: DateTime.UtcNow.AddDays(-3)) { Lapses = lapses });
        await userDb.SaveChangesAsync();
    }

    private async Task<FsrsCard> GetCard(int wordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsCards.AsNoTracking().FirstAsync(c => c.UserId == TestUsers.UserA && c.WordId == wordId);
    }

    private Task<HttpResponseMessage> Post(string path, object body)
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, path).WithUser(TestUsers.UserA).WithJsonContent(body));

    [Fact]
    public async Task ResetSchedule_ClearsLapses_OnEveryPath()
    {
        await SeedLeech(1);
        await SeedLeech(2);
        await SeedLeech(3);

        (await Post("/api/srs/set-vocabulary-state", new { wordId = 1, readingIndex = 0, state = "reset-schedule" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Post("/api/srs/set-vocabulary-state-bulk", new { state = "reset-schedule", items = new[] { new { wordId = 2, readingIndex = 0 } } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Post("/api/srs/mass-action/execute", new { action = "reset-schedule", stateFilter = new[] { (int)FsrsState.Review } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var wordId in new[] { 1, 2, 3 })
        {
            var card = await GetCard(wordId);
            card.Lapses.Should().Be(0);
            card.State.Should().Be(FsrsState.Learning);
            card.Stability.Should().BeNull();
        }
    }

    [Fact]
    public async Task ClearLapses_KeepsTheSchedule_OnEveryPath()
    {
        await SeedLeech(1);
        await SeedLeech(2);
        await SeedLeech(3);

        (await Post("/api/srs/set-vocabulary-state", new { wordId = 1, readingIndex = 0, state = "clear-lapses" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Post("/api/srs/set-vocabulary-state-bulk", new { state = "clear-lapses", items = new[] { new { wordId = 2, readingIndex = 0 } } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Post("/api/srs/mass-action/execute", new { action = "clear-lapses" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var wordId in new[] { 1, 2, 3 })
        {
            var card = await GetCard(wordId);
            card.Lapses.Should().Be(0);
            card.State.Should().Be(FsrsState.Review);
            card.Stability.Should().Be(4);
            card.Difficulty.Should().Be(8);
            card.LastReview.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task ClearLapses_MassAction_CountsOnlyLapsedCards()
    {
        await SeedLeech(1, lapses: 3);
        await SeedLeech(2, lapses: 0);

        var preview = await Post("/api/srs/mass-action/preview", new { action = "clear-lapses" });
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        var previewBody = await preview.Content.ReadFromJsonAsync<JsonElement>();
        previewBody.GetProperty("totalItems").GetInt32().Should().Be(1);

        var execute = await Post("/api/srs/mass-action/execute", new { action = "clear-lapses" });
        var executeBody = await execute.Content.ReadFromJsonAsync<JsonElement>();
        executeBody.GetProperty("affectedCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ClearLapses_Bulk_ReportsOnlyLapsedCards()
    {
        await SeedLeech(1, lapses: 3);
        await SeedLeech(2, lapses: 0);

        var response = await Post("/api/srs/set-vocabulary-state-bulk",
                                  new { state = "clear-lapses", items = new[] { new { wordId = 1, readingIndex = 0 }, new { wordId = 2, readingIndex = 0 } } });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("affectedCount").GetInt32().Should().Be(1);
    }
}
