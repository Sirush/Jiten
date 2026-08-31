using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class RequestTurnaroundTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();
    private static readonly DateTime Now = DateTime.UtcNow;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMemoryCache>().Remove("requests:turnaround");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedRequest(MediaRequestStatus status, double createdDaysAgo, double? completedDaysAgo = null,
                                        double? uploadedDaysAgo = null, bool uploadDeleted = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var request = new MediaRequest
        {
            Title = $"Turnaround {Guid.NewGuid():N}",
            MediaType = MediaType.Anime,
            Status = status,
            RequesterId = TestUsers.UserA,
            CreatedAt = Now.AddDays(-createdDaysAgo),
            UpdatedAt = Now.AddDays(-createdDaysAgo),
            CompletedAt = completedDaysAgo.HasValue ? Now.AddDays(-completedDaysAgo.Value) : null,
        };
        db.MediaRequests.Add(request);
        await db.SaveChangesAsync();

        if (uploadedDaysAgo.HasValue)
        {
            var comment = new MediaRequestComment
            {
                MediaRequestId = request.Id,
                UserId = TestUsers.UserA,
                Text = "file",
                CreatedAt = Now.AddDays(-uploadedDaysAgo.Value),
            };
            db.MediaRequestComments.Add(comment);
            await db.SaveChangesAsync();

            db.MediaRequestUploads.Add(new MediaRequestUpload
            {
                MediaRequestCommentId = comment.Id,
                MediaRequestId = request.Id,
                FileName = "script.txt",
                StoragePath = $"uploads/{request.Id}.txt",
                FileSize = 1024,
                CreatedAt = Now.AddDays(-uploadedDaysAgo.Value),
                FileDeleted = uploadDeleted,
            });
            await db.SaveChangesAsync();
        }

        return request.Id;
    }

    private async Task<JsonElement> GetTurnaround()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/requests/turnaround").WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Quantiles_CountOpenRequestsAsCensoredRatherThanDroppingThem()
    {
        // Upload-to-completion of 2, 4, 6 and 8 days, plus one request still open 100 days after its upload.
        await SeedRequest(MediaRequestStatus.Completed, 20, completedDaysAgo: 18, uploadedDaysAgo: 20);
        await SeedRequest(MediaRequestStatus.Completed, 20, completedDaysAgo: 16, uploadedDaysAgo: 20);
        await SeedRequest(MediaRequestStatus.Completed, 20, completedDaysAgo: 14, uploadedDaysAgo: 20);
        await SeedRequest(MediaRequestStatus.Completed, 20, completedDaysAgo: 12, uploadedDaysAgo: 20);
        await SeedRequest(MediaRequestStatus.Open, 100, uploadedDaysAgo: 100);

        var body = await GetTurnaround();

        body.GetProperty("sampleSize").GetInt32().Should().Be(5);
        body.GetProperty("medianDays").GetDouble().Should().BeApproximately(6, 0.01);
        body.GetProperty("p75Days").GetDouble().Should().BeApproximately(8, 0.01);
    }

    [Fact]
    public async Task Quantile_IsNullWhenFollowUpNeverReachesIt()
    {
        await SeedRequest(MediaRequestStatus.Completed, 20, completedDaysAgo: 18, uploadedDaysAgo: 20);
        await SeedRequest(MediaRequestStatus.Open, 100, uploadedDaysAgo: 100);
        await SeedRequest(MediaRequestStatus.Open, 100, uploadedDaysAgo: 100);
        await SeedRequest(MediaRequestStatus.Open, 100, uploadedDaysAgo: 100);

        var body = await GetTurnaround();

        body.GetProperty("medianDays").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("p75Days").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ReopenedRequest_IsCensoredAtNowRatherThanItsStaleCompletedAt()
    {
        // Reopening leaves CompletedAt populated, so a stale value must not be read back as a fulfilment.
        await SeedRequest(MediaRequestStatus.Open, 100, completedDaysAgo: 99, uploadedDaysAgo: 100);
        await SeedRequest(MediaRequestStatus.Completed, 20, completedDaysAgo: 10, uploadedDaysAgo: 20);

        var body = await GetTurnaround();

        body.GetProperty("sampleSize").GetInt32().Should().Be(2);
        body.GetProperty("medianDays").GetDouble().Should().BeApproximately(10, 0.01);
    }

    [Fact]
    public async Task Buckets_SplitOpenRequestsByWhetherAFileSurvives()
    {
        await SeedRequest(MediaRequestStatus.Open, 10);
        await SeedRequest(MediaRequestStatus.Open, 30);
        await SeedRequest(MediaRequestStatus.Open, 50);
        await SeedRequest(MediaRequestStatus.Open, 40, uploadedDaysAgo: 5, uploadDeleted: true);
        await SeedRequest(MediaRequestStatus.InProgress, 12, uploadedDaysAgo: 6);
        await SeedRequest(MediaRequestStatus.Completed, 20, completedDaysAgo: 10, uploadedDaysAgo: 20);

        var body = await GetTurnaround();

        body.GetProperty("readyToProcess").GetInt32().Should().Be(1);
        body.GetProperty("awaitingFile").GetInt32().Should().Be(4);
        body.GetProperty("medianAwaitingFileDays").GetDouble().Should().BeApproximately(30, 0.01);
    }
}
