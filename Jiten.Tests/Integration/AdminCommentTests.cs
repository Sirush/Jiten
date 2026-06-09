using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class AdminCommentTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> CreateRequest(string userId = TestUsers.UserA)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/requests")
            .WithUser(userId)
            .WithJsonContent(new { title = "Admin Comment Test", mediaType = (int)MediaType.Anime });
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    private async Task<int> AddComment(int requestId, string userId, string text)
    {
        var content = new MultipartFormDataContent { { new StringContent(text), "text" } };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{requestId}/comments") { Content = content };
        request.WithUser(userId);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    private async Task CompleteRequest(int requestId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var deck = new Deck { OriginalTitle = "Fulfilled", MediaType = MediaType.Anime };
        db.Decks.Add(deck);
        await db.SaveChangesAsync();

        var statusRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{requestId}/status")
            .WithAdmin()
            .WithJsonContent(new { status = (int)MediaRequestStatus.Completed, fulfilledDeckId = deck.DeckId });
        var response = await _client.SendAsync(statusRequest);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<int> AddAdminComment(int requestId, int parentCommentId, string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{requestId}/comments/{parentCommentId}/admin-comment")
            .WithAdmin()
            .WithJsonContent(new { text });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task AdminComment_NestsUnderParent_AndNotifiesAuthor()
    {
        var id = await CreateRequest(TestUsers.UserA);
        var commentId = await AddComment(id, TestUsers.UserB, "Here is my upload");

        await AddAdminComment(id, commentId, "This upload is corrupt.");

        // GetComments (as the parent author) nests the admin note under the parent comment
        var getRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/requests/{id}/comments")
            .WithUser(TestUsers.UserB);
        var getResponse = await _client.SendAsync(getRequest);
        var comments = (await getResponse.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();

        comments.Should().HaveCount(1); // admin note is nested, not a top-level comment
        var parent = comments[0];
        parent.GetProperty("role").GetString().Should().Be("Contributor");
        var notes = parent.GetProperty("adminComments").EnumerateArray().ToList();
        notes.Should().HaveCount(1);
        notes[0].GetProperty("role").GetString().Should().Be("Admin");
        notes[0].GetProperty("isAdminComment").GetBoolean().Should().BeTrue();
        notes[0].GetProperty("text").GetString().Should().Be("This upload is corrupt.");

        // The parent comment's author is notified
        var notifRequest = new HttpRequestMessage(HttpMethod.Get, "/api/notifications")
            .WithUser(TestUsers.UserB);
        var notifResponse = await _client.SendAsync(notifRequest);
        var notifData = (await notifResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        notifData.EnumerateArray()
            .Should().Contain(n => n.GetProperty("title").GetString() == "An admin replied to your comment");
    }

    [Fact]
    public async Task AdminComment_FromNonAdmin_Forbidden()
    {
        var id = await CreateRequest(TestUsers.UserA);
        var commentId = await AddComment(id, TestUsers.UserB, "Here is my upload");

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{id}/comments/{commentId}/admin-comment")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { text = "I am not an admin" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminComment_AllowedOnCompletedRequest()
    {
        var id = await CreateRequest(TestUsers.UserA);
        var commentId = await AddComment(id, TestUsers.UserB, "Here is my upload");
        await CompleteRequest(id);

        // Admin can still annotate even though normal comments are blocked on completed requests
        await AddAdminComment(id, commentId, "Rejected: wrong volume.");
    }

    [Fact]
    public async Task AdminComment_EditableOnCompletedRequest()
    {
        var id = await CreateRequest(TestUsers.UserA);
        var commentId = await AddComment(id, TestUsers.UserB, "Here is my upload");
        var adminCommentId = await AddAdminComment(id, commentId, "Initial note");
        await CompleteRequest(id);

        var editRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/comments/{adminCommentId}")
            .WithAdmin()
            .WithJsonContent(new { text = "Updated note" });
        var response = await _client.SendAsync(editRequest);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AdminComment_OnAdminComment_Returns400()
    {
        var id = await CreateRequest(TestUsers.UserA);
        var commentId = await AddComment(id, TestUsers.UserB, "Here is my upload");
        var adminCommentId = await AddAdminComment(id, commentId, "First note");

        // Replying to an admin note (not a top-level comment) is rejected
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{id}/comments/{adminCommentId}/admin-comment")
            .WithAdmin()
            .WithJsonContent(new { text = "Nested note" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
