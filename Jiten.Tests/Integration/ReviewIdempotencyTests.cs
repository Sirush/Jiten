using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
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

public class ReviewClaimTests(JitenWebApplicationFactory factory)
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

    private IStudySessionService Sessions => factory.Services.GetRequiredService<IStudySessionService>();

    private static HttpRequestMessage Review(int wordId, int rating, string? clientRequestId = null, string? sessionId = null)
        => new HttpRequestMessage(HttpMethod.Post, "/api/srs/review")
           .WithUser(TestUsers.UserA)
           .WithJsonContent(new { wordId, readingIndex = 0, rating, clientRequestId, sessionId });

    private static HttpRequestMessage Batch(int[] wordIds, string? clientRequestId = null, string? sessionId = null)
        => new HttpRequestMessage(HttpMethod.Post, "/api/srs/batch-review")
           .WithUser(TestUsers.UserA)
           .WithJsonContent(new
           {
               reviews = wordIds.Select(w => new { wordId = w, readingIndex = 0, rating = 3 }).ToArray(),
               clientRequestId,
               sessionId
           });

    private async Task<int> LogCount(int wordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsReviewLogs.CountAsync(l => l.Card.UserId == TestUsers.UserA && l.Card.WordId == wordId);
    }

    private async Task SeedSuspended(int wordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, 0, state: FsrsState.Suspended, stability: 5, difficulty: 5,
                                          due: DateTime.UtcNow, lastReview: DateTime.UtcNow.AddDays(-1)));
        await userDb.SaveChangesAsync();
    }

    [Fact]
    public async Task Claim_SecondClaimWhileInFlight_IsRejected_ThenCompletesAfterStore()
    {
        var scope = "user:claim";
        (await Sessions.TryClaimReview(scope, "a")).Status.Should().Be(ReviewClaimStatus.Acquired);
        (await Sessions.TryClaimReview(scope, "a")).Status.Should().Be(ReviewClaimStatus.InFlight);

        await Sessions.StoreCachedReviewResult(scope, "a", "{\"success\":true}");
        var done = await Sessions.TryClaimReview(scope, "a");
        done.Status.Should().Be(ReviewClaimStatus.Completed);
        done.ResultJson.Should().Be("{\"success\":true}");

        await Sessions.ReleaseReviewClaim(scope, "b");
        (await Sessions.TryClaimReview(scope, "b")).Status.Should().Be(ReviewClaimStatus.Acquired);
        await Sessions.ReleaseReviewClaim(scope, "b");
        (await Sessions.TryClaimReview(scope, "b")).Status.Should().Be(ReviewClaimStatus.Acquired);
    }

    [Fact]
    public async Task ConcurrentResends_WithOneClientRequestId_RecordOneLog()
    {
        var id = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _client.SendAsync(Review(10, 3, id))));

        responses.Select(r => r.StatusCode).Should().OnlyContain(s => s == HttpStatusCode.OK || s == HttpStatusCode.Conflict);
        responses.Should().Contain(r => r.StatusCode == HttpStatusCode.OK);
        (await LogCount(10)).Should().Be(1);
    }

    [Fact]
    public async Task RejectedReview_ReleasesTheClaim_SoTheRetryIsNotReportedInFlight()
    {
        await SeedSuspended(11);
        var id = Guid.NewGuid().ToString("N");

        (await _client.SendAsync(Review(11, 3, id))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.SendAsync(Review(11, 3, id))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BatchResend_WithTheSameClientRequestId_IsAppliedOnce()
    {
        var id = Guid.NewGuid().ToString("N");

        var first = await _client.SendAsync(Batch([20, 21], id));
        var second = await _client.SendAsync(Batch([20, 21], id));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadAsStringAsync()).Should().Be(await first.Content.ReadAsStringAsync());
        (await LogCount(20)).Should().Be(1);
        (await LogCount(21)).Should().Be(1);
    }

    [Fact]
    public async Task IdLessBatchResend_WithinTheDebounceWindow_Returns409()
    {
        Debounce.Enabled = true;

        var first = await _client.SendAsync(Batch([22, 23]));
        var duplicate = await _client.SendAsync(Batch([22, 23]));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await LogCount(22)).Should().Be(1);
    }

    [Fact]
    public async Task Batch_WithAnotherUsersSession_IsUnauthorized()
    {
        var foreignSession = await Sessions.CreateSession(TestUsers.UserB);

        var response = await _client.SendAsync(Batch([24], sessionId: foreignSession));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await LogCount(24)).Should().Be(0);
    }
}
