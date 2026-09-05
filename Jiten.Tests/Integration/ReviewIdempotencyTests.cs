using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ReviewIdempotencyTests(JitenWebApplicationFactory factory)
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

    public Task DisposeAsync()
    {
        Debounce.Enabled = false;
        return Task.CompletedTask;
    }

    private NoOpSrsDebounceService Debounce
        => (NoOpSrsDebounceService)factory.Services.GetRequiredService<ISrsDebounceService>();

    private static HttpRequestMessage Review(int wordId, int rating, string? clientRequestId = null)
        => new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
           .WithUser(TestUsers.UserA)
           .WithJsonContent(new { wordId, readingIndex = 0, rating, clientRequestId });

    private async Task<int> LogCount(int wordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsReviewLogs.CountAsync(l => l.Card.UserId == TestUsers.UserA && l.Card.WordId == wordId);
    }

    [Fact]
    public async Task Resend_WithTheSameClientRequestId_AndNoSession_ReturnsTheStoredResultOnce()
    {
        var id = Guid.NewGuid().ToString("N");

        var first = await _client.SendAsync(Review(1, 3, id));
        var second = await _client.SendAsync(Review(1, 3, id));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("success").GetBoolean().Should().BeTrue();
        (await LogCount(1)).Should().Be(1);
    }

    [Fact]
    public async Task DifferentClientRequestIds_WithinTheDebounceWindow_AreBothRecorded()
    {
        Debounce.Enabled = true;
        var client = _client;

        var first = await client.SendAsync(Review(2, 3, Guid.NewGuid().ToString("N")));
        var undo = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/undo-review")
                                          .WithUser(TestUsers.UserA)
                                          .WithJsonContent(new { wordId = 2, readingIndex = 0 }));
        var regrade = await client.SendAsync(Review(2, 1, Guid.NewGuid().ToString("N")));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        undo.StatusCode.Should().Be(HttpStatusCode.OK);
        regrade.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LogCount(2)).Should().Be(1, "the undo removed the first grade and the re-grade landed");
    }

    [Fact]
    public async Task IdLessDuplicate_WithinTheDebounceWindow_Returns409_WithAnErrorBody()
    {
        Debounce.Enabled = true;
        var client = _client;

        var first = await client.SendAsync(Review(3, 3));
        var duplicate = await client.SendAsync(Review(3, 3));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await duplicate.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error_message").GetString().Should().NotBeNullOrEmpty();
        (await LogCount(3)).Should().Be(1);
    }
}
