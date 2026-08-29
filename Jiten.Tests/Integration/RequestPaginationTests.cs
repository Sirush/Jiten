using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class RequestPaginationTests(JitenWebApplicationFactory factory)
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

    private async Task<(List<string> Titles, int TotalItems)> GetPage(string flag, int limit, int offset)
    {
        var url = $"/api/requests?{flag}=true&sort=recent&limit={limit}&offset={offset}";
        var request = new HttpRequestMessage(HttpMethod.Get, url).WithUser(TestUsers.UserA);
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var titles = body.GetProperty("data").EnumerateArray()
            .Select(r => r.GetProperty("title").GetString()!)
            .ToList();
        return (titles, body.GetProperty("totalItems").GetInt32());
    }

    private async Task SeedOwnRequests()
    {
        for (var i = 1; i <= 5; i++)
            await CreateRequest(TestUsers.UserA, $"Mine {i}");
    }

    [Fact]
    public async Task Mine_RespectsOffsetAndLimit()
    {
        await SeedOwnRequests();

        var all = await GetPage("mine", limit: 200, offset: 0);
        var page = await GetPage("mine", limit: 2, offset: 2);

        all.Titles.Should().HaveCount(5);
        page.Titles.Should().Equal(all.Titles.Skip(2).Take(2));
    }

    [Fact]
    public async Task Mine_TotalCountIgnoresPaging()
    {
        await SeedOwnRequests();

        var page = await GetPage("mine", limit: 2, offset: 0);

        page.Titles.Should().HaveCount(2);
        page.TotalItems.Should().Be(5);
    }

    [Fact]
    public async Task Mine_OffsetPastEnd_ReturnsEmptyWithTotal()
    {
        await SeedOwnRequests();

        var page = await GetPage("mine", limit: 2, offset: 20);

        page.Titles.Should().BeEmpty();
        page.TotalItems.Should().Be(5);
    }

    [Fact]
    public async Task Contributed_RespectsOffsetAndLimit()
    {
        for (var i = 1; i <= 5; i++)
        {
            var id = await CreateRequest(TestUsers.UserB, $"Theirs {i}");
            await SeedUpload(factory, id, TestUsers.UserA);
        }

        var all = await GetPage("contributed", limit: 200, offset: 0);
        var page = await GetPage("contributed", limit: 2, offset: 2);

        all.Titles.Should().HaveCount(5);
        all.TotalItems.Should().Be(5);
        page.TotalItems.Should().Be(5);
        page.Titles.Should().Equal(all.Titles.Skip(2).Take(2));
    }
}
