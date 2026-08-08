using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class CommentTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> CreateRequest(string userId = TestUsers.UserA)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/requests")
            .WithUser(userId)
            .WithJsonContent(new { title = "Comment Test", mediaType = (int)MediaType.Anime });
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task AddTextComment_WhenOpen_Returns200()
    {
        var id = await CreateRequest();

        var content = new MultipartFormDataContent();
        content.Add(new StringContent("Great request!"), "text");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{id}/comments")
        {
            Content = content
        };
        request.WithUser(TestUsers.UserB);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AddComment_WhenCompleted_Returns400()
    {
        var id = await CreateRequest();

        // Seed a deck and complete the request
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var deck = new Deck { OriginalTitle = "Fulfilled", MediaType = MediaType.Anime };
        db.Decks.Add(deck);
        await db.SaveChangesAsync();

        var statusRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status")
            .WithAdmin()
            .WithJsonContent(new { status = (int)MediaRequestStatus.Completed, fulfilledDeckId = deck.DeckId });
        await _client.SendAsync(statusRequest);

        // Try to comment
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("Late comment"), "text");

        var commentRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{id}/comments")
        {
            Content = content
        };
        commentRequest.WithUser(TestUsers.UserB);

        var response = await _client.SendAsync(commentRequest);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EmptyComment_Returns400()
    {
        var id = await CreateRequest();

        var content = new MultipartFormDataContent();

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{id}/comments")
        {
            Content = content
        };
        request.WithUser(TestUsers.UserB);

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CommentRole_RequesterGetsRequester_OtherGetsContributor()
    {
        var id = await CreateRequest(TestUsers.UserA);

        // Requester comments
        var c1 = new MultipartFormDataContent();
        c1.Add(new StringContent("Requester comment"), "text");
        var r1 = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{id}/comments") { Content = c1 };
        r1.WithUser(TestUsers.UserA);
        await _client.SendAsync(r1);

        // Contributor comments
        var c2 = new MultipartFormDataContent();
        c2.Add(new StringContent("Contributor comment"), "text");
        var r2 = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{id}/comments") { Content = c2 };
        r2.WithUser(TestUsers.UserB);
        await _client.SendAsync(r2);

        // Get comments
        var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/requests/{id}/comments")
            .WithUser(TestUsers.UserA);
        var getResponse = await _client.SendAsync(getRequest);
        var comments = await getResponse.Content.ReadFromJsonAsync<JsonElement>();

        var arr = comments.EnumerateArray().ToList();
        arr.Should().HaveCount(2);
        arr[0].GetProperty("role").GetString().Should().Be("Requester");
        arr[1].GetProperty("role").GetString().Should().Be("Contributor");
    }

    private async Task SeedUpload(int requestId, string fileName, int originalFileCount)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var comment = new MediaRequestComment { MediaRequestId = requestId, UserId = TestUsers.UserB };
        db.MediaRequestComments.Add(comment);
        await db.SaveChangesAsync();

        db.MediaRequestUploads.Add(new MediaRequestUpload
        {
            MediaRequestCommentId = comment.Id,
            MediaRequestId = requestId,
            FileName = fileName,
            StoragePath = $"uploads/{requestId}/bundle.zip",
            FileSize = 2048,
            OriginalFileCount = originalFileCount
        });
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> GetUploadDto(HttpRequestMessage getRequest)
    {
        var response = await _client.SendAsync(getRequest);
        var comments = await response.Content.ReadFromJsonAsync<JsonElement>();
        return comments.EnumerateArray().Single().GetProperty("upload");
    }

    [Fact]
    public async Task GetComments_AsNonAdmin_OmitsUploadFileName()
    {
        var id = await CreateRequest(TestUsers.UserA);
        await SeedUpload(id, "とある作品 01-03.txt", 3);

        var upload = await GetUploadDto(
            new HttpRequestMessage(HttpMethod.Get, $"/api/requests/{id}/comments").WithUser(TestUsers.UserA));

        upload.TryGetProperty("fileName", out _).Should().BeFalse();
        upload.GetProperty("originalFileCount").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task GetComments_AsAdmin_IncludesUploadFileName()
    {
        var id = await CreateRequest(TestUsers.UserA);
        await SeedUpload(id, "とある作品 01-03.txt", 3);

        var upload = await GetUploadDto(
            new HttpRequestMessage(HttpMethod.Get, $"/api/requests/{id}/comments").WithAdmin());

        upload.GetProperty("fileName").GetString().Should().Be("とある作品 01-03.txt");
    }
}
