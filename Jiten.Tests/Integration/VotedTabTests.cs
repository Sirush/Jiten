using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class VotedTabTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> CreateRequest(string userId, string title, MediaType mediaType = MediaType.Anime)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/requests")
            .WithUser(userId)
            .WithJsonContent(new { title, mediaType = (int)mediaType });
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    private async Task Upvote(int requestId, string userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{requestId}/upvote").WithUser(userId);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task SeedUpload(JitenWebApplicationFactory factory, int requestId, string uploaderId)
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
            FileSize = 1024
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<string>> GetVotedTitles(string userId = TestUsers.UserA, bool excludeOwn = false, string extraQuery = "")
    {
        var url = $"/api/requests?voted=true&sort=recent&limit=200{(excludeOwn ? "&excludeOwn=true" : "")}{extraQuery}";
        var request = new HttpRequestMessage(HttpMethod.Get, url).WithUser(userId);
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").EnumerateArray()
            .Select(r => r.GetProperty("title").GetString()!)
            .ToList();
    }

    [Fact]
    public async Task Voted_ReturnsOnlyUpvotedRequests()
    {
        var upvoted = await CreateRequest(TestUsers.UserB, "Voted For");
        await CreateRequest(TestUsers.UserB, "Ignored");
        await Upvote(upvoted, TestUsers.UserA);

        var titles = await GetVotedTitles();

        titles.Should().BeEquivalentTo("Voted For");
    }

    [Fact]
    public async Task Voted_IncludesOwnRequestsByDefault()
    {
        await CreateRequest(TestUsers.UserA, "My Own");
        var other = await CreateRequest(TestUsers.UserB, "Someone Else's");
        await Upvote(other, TestUsers.UserA);

        var titles = await GetVotedTitles();

        titles.Should().BeEquivalentTo("My Own", "Someone Else's");
    }

    [Fact]
    public async Task Voted_WithExcludeOwn_DropsOwnRequests()
    {
        await CreateRequest(TestUsers.UserA, "My Own");
        var other = await CreateRequest(TestUsers.UserB, "Someone Else's");
        await Upvote(other, TestUsers.UserA);

        var titles = await GetVotedTitles(excludeOwn: true);

        titles.Should().BeEquivalentTo("Someone Else's");
    }

    [Fact]
    public async Task Voted_AfterUnUpvote_DropsRequest()
    {
        var id = await CreateRequest(TestUsers.UserB, "Changed My Mind");
        await Upvote(id, TestUsers.UserA);
        await Upvote(id, TestUsers.UserA);

        var titles = await GetVotedTitles();

        titles.Should().BeEmpty();
    }

    [Fact]
    public async Task Voted_IsScopedToCaller()
    {
        var id = await CreateRequest(TestUsers.UserB, "UserB's Pick");
        await Upvote(id, TestUsers.UserB);

        var titles = await GetVotedTitles();

        titles.Should().BeEmpty();
    }

    [Fact]
    public async Task Voted_RespectsAttachmentFilter()
    {
        var withFiles = await CreateRequest(TestUsers.UserB, "Already Handled");
        var withoutFiles = await CreateRequest(TestUsers.UserB, "Still Nothing");
        await Upvote(withFiles, TestUsers.UserA);
        await Upvote(withoutFiles, TestUsers.UserA);
        await SeedUpload(factory, withFiles, TestUsers.UserB);

        var titles = await GetVotedTitles(extraQuery: "&attachments=no");

        titles.Should().BeEquivalentTo("Still Nothing");
    }

    [Fact]
    public async Task VotedFacets_CountWithinVotedScope()
    {
        var anime = await CreateRequest(TestUsers.UserB, "Anime Pick", MediaType.Anime);
        await CreateRequest(TestUsers.UserB, "Novel Nobody Voted", MediaType.Novel);
        await Upvote(anime, TestUsers.UserA);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/requests/facets?voted=true").WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("mediaTypeTotal").GetInt32().Should().Be(1);
        body.GetProperty("mediaTypes").GetProperty(((int)MediaType.Anime).ToString()).GetInt32().Should().Be(1);
        body.GetProperty("mediaTypes").TryGetProperty(((int)MediaType.Novel).ToString(), out _).Should().BeFalse();
    }
}
