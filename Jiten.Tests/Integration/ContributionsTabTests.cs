using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ContributionsTabTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> CreateRequest(string userId, string title)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/requests")
            .WithUser(userId)
            .WithJsonContent(new { title, mediaType = (int)MediaType.Anime });
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    private static async Task SeedUpload(JitenWebApplicationFactory factory, int requestId, string uploaderId, bool fileDeleted = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var comment = new MediaRequestComment { MediaRequestId = requestId, UserId = uploaderId, Text = "Here you go" };
        db.MediaRequestComments.Add(comment);
        await db.SaveChangesAsync();

        db.MediaRequestUploads.Add(new MediaRequestUpload
        {
            MediaRequestCommentId = comment.Id,
            MediaRequestId = requestId,
            FileName = "script.zip",
            StoragePath = $"uploads/{requestId}/script.zip",
            FileSize = 1024,
            FileDeleted = fileDeleted
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> GetContributionTitles(bool excludeOwn)
    {
        var url = $"/api/requests?contributed=true&sort=recent&limit=200{(excludeOwn ? "&excludeOwn=true" : "")}";
        var request = new HttpRequestMessage(HttpMethod.Get, url).WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").EnumerateArray()
            .Select(r => r.GetProperty("title").GetString()!)
            .ToList();
    }

    [Fact]
    public async Task Contributed_IncludesUploadsToOwnRequests()
    {
        var ownRequestId = await CreateRequest(TestUsers.UserA, "Self Fulfilled");
        var otherRequestId = await CreateRequest(TestUsers.UserB, "Someone Else's");
        await SeedUpload(factory, ownRequestId, TestUsers.UserA);
        await SeedUpload(factory, otherRequestId, TestUsers.UserA);

        var titles = await GetContributionTitles(excludeOwn: false);

        titles.Should().BeEquivalentTo("Self Fulfilled", "Someone Else's");
    }

    [Fact]
    public async Task Contributed_WithExcludeOwn_DropsOwnRequests()
    {
        var ownRequestId = await CreateRequest(TestUsers.UserA, "Self Fulfilled");
        var otherRequestId = await CreateRequest(TestUsers.UserB, "Someone Else's");
        await SeedUpload(factory, ownRequestId, TestUsers.UserA);
        await SeedUpload(factory, otherRequestId, TestUsers.UserA);

        var titles = await GetContributionTitles(excludeOwn: true);

        titles.Should().BeEquivalentTo("Someone Else's");
    }

    [Fact]
    public async Task Contributed_ExcludesTextOnlyCommentsAndDeletedFiles()
    {
        var commentedOnly = await CreateRequest(TestUsers.UserB, "Commented Only");
        var deletedUpload = await CreateRequest(TestUsers.UserB, "Deleted Upload");
        var kept = await CreateRequest(TestUsers.UserB, "Kept Upload");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            db.MediaRequestComments.Add(new MediaRequestComment
            {
                MediaRequestId = commentedOnly, UserId = TestUsers.UserA, Text = "Seconded"
            });
            await db.SaveChangesAsync();
        }

        await SeedUpload(factory, deletedUpload, TestUsers.UserA, fileDeleted: true);
        await SeedUpload(factory, kept, TestUsers.UserA);

        var titles = await GetContributionTitles(excludeOwn: false);

        titles.Should().BeEquivalentTo("Kept Upload");
    }

    [Fact]
    public async Task Contributed_ExcludesOtherUsersUploads()
    {
        var requestId = await CreateRequest(TestUsers.UserB, "Someone Else's");
        await SeedUpload(factory, requestId, TestUsers.UserB);

        var titles = await GetContributionTitles(excludeOwn: false);

        titles.Should().BeEmpty();
    }
}
