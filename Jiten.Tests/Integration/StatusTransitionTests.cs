using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class StatusTransitionTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> CreateRequest(string userId = TestUsers.UserA)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/requests")
            .WithUser(userId)
            .WithJsonContent(new { title = "Status Test", mediaType = (int)MediaType.Anime });
        var response = await _client.SendAsync(request);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    private async Task SetStatus(int id, MediaRequestStatus status, string? adminNote = null, int? fulfilledDeckId = null)
    {
        var payload = new Dictionary<string, object?> { ["status"] = (int)status };
        if (adminNote != null) payload["adminNote"] = adminNote;
        if (fulfilledDeckId.HasValue) payload["fulfilledDeckId"] = fulfilledDeckId.Value;

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status")
            .WithAdmin()
            .WithJsonContent(payload);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            $"expected status transition to {status} to succeed");
    }

    private async Task<int> SeedDeck()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var deck = new Deck { OriginalTitle = "Test Deck", MediaType = MediaType.Anime };
        db.Decks.Add(deck);
        await db.SaveChangesAsync();
        return deck.DeckId;
    }

    [Theory]
    [InlineData(MediaRequestStatus.InProgress)]
    [InlineData(MediaRequestStatus.Rejected)]
    public async Task ValidTransition_FromOpen_Succeeds(MediaRequestStatus target)
    {
        var id = await CreateRequest();

        var payload = new Dictionary<string, object?> { ["status"] = (int)target };
        if (target == MediaRequestStatus.Rejected) payload["adminNote"] = "Not suitable";

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status")
            .WithAdmin()
            .WithJsonContent(payload);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ValidTransition_FromOpen_ToCompleted_WithDeck()
    {
        var id = await CreateRequest();
        var deckId = await SeedDeck();

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status")
            .WithAdmin()
            .WithJsonContent(new { status = (int)MediaRequestStatus.Completed, fulfilledDeckId = deckId });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ValidTransition_FromInProgress_ToCompleted()
    {
        var id = await CreateRequest();
        var deckId = await SeedDeck();

        await SetStatus(id, MediaRequestStatus.InProgress);

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status")
            .WithAdmin()
            .WithJsonContent(new { status = (int)MediaRequestStatus.Completed, fulfilledDeckId = deckId });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ValidTransition_FromInProgress_ToRejected()
    {
        var id = await CreateRequest();
        await SetStatus(id, MediaRequestStatus.InProgress);

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status")
            .WithAdmin()
            .WithJsonContent(new { status = (int)MediaRequestStatus.Rejected, adminNote = "Duplicate" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(MediaRequestStatus.Completed, MediaRequestStatus.Open)]
    [InlineData(MediaRequestStatus.Rejected, MediaRequestStatus.Open)]
    public async Task ValidTransition_ReopenFromTerminal_Succeeds(MediaRequestStatus from, MediaRequestStatus to)
    {
        var id = await CreateRequest();
        var deckId = await SeedDeck();

        if (from == MediaRequestStatus.Completed)
            await SetStatus(id, MediaRequestStatus.Completed, fulfilledDeckId: deckId);
        else
            await SetStatus(id, MediaRequestStatus.Rejected, adminNote: "Rejected");

        var payload = new Dictionary<string, object?> { ["status"] = (int)to };
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status")
            .WithAdmin()
            .WithJsonContent(payload);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(MediaRequestStatus.Completed, MediaRequestStatus.InProgress)]
    [InlineData(MediaRequestStatus.Rejected, MediaRequestStatus.InProgress)]
    public async Task InvalidTransition_Returns409(MediaRequestStatus from, MediaRequestStatus to)
    {
        var id = await CreateRequest();
        var deckId = await SeedDeck();

        if (from == MediaRequestStatus.Completed)
            await SetStatus(id, MediaRequestStatus.Completed, fulfilledDeckId: deckId);
        else if (from == MediaRequestStatus.Rejected)
            await SetStatus(id, MediaRequestStatus.Rejected, adminNote: "Rejected");

        var payload = new Dictionary<string, object?> { ["status"] = (int)to };
        if (to == MediaRequestStatus.Rejected) payload["adminNote"] = "Some note";
        if (to == MediaRequestStatus.Completed) payload["fulfilledDeckId"] = deckId;

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status")
            .WithAdmin()
            .WithJsonContent(payload);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SameStatusIdempotency_Returns200()
    {
        var id = await CreateRequest();

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status")
            .WithAdmin()
            .WithJsonContent(new { status = (int)MediaRequestStatus.Open });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Completed_WithoutDeckId_Returns400()
    {
        var id = await CreateRequest();

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status")
            .WithAdmin()
            .WithJsonContent(new { status = (int)MediaRequestStatus.Completed });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rejected_WithoutAdminNote_Returns400()
    {
        var id = await CreateRequest();

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status")
            .WithAdmin()
            .WithJsonContent(new { status = (int)MediaRequestStatus.Rejected });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NonAdmin_Returns403()
    {
        var id = await CreateRequest();

        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { status = (int)MediaRequestStatus.InProgress });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Completed_SetsCompletedAt()
    {
        var id = await CreateRequest();
        var deckId = await SeedDeck();

        await SetStatus(id, MediaRequestStatus.Completed, fulfilledDeckId: deckId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var request = await db.MediaRequests.FindAsync(id);
        request!.CompletedAt.Should().NotBeNull();
        request.CompletedAt!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Rejected_SetsCompletedAt()
    {
        var id = await CreateRequest();

        await SetStatus(id, MediaRequestStatus.Rejected, adminNote: "Not suitable");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var request = await db.MediaRequests.FindAsync(id);
        request!.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task GetRequest_ExposesCompletedAt_WhenCompleted()
    {
        var id = await CreateRequest();
        var deckId = await SeedDeck();

        await SetStatus(id, MediaRequestStatus.Completed, fulfilledDeckId: deckId);

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/requests/{id}").WithUser(TestUsers.UserA));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("completedAt", out var completedAt).Should().BeTrue();
        completedAt.ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public async Task GetRequest_CompletedAt_IsNull_WhenOpen()
    {
        var id = await CreateRequest();

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/requests/{id}").WithUser(TestUsers.UserA));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (body.TryGetProperty("completedAt", out var completedAt))
            completedAt.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Completed_SendsNotificationToSubscribers()
    {
        var id = await CreateRequest(TestUsers.UserA);
        var deckId = await SeedDeck();

        // UserB subscribes
        var sub = new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{id}/subscribe")
            .WithUser(TestUsers.UserB);
        await _client.SendAsync(sub);

        // Admin completes
        await SetStatus(id, MediaRequestStatus.Completed, fulfilledDeckId: deckId);

        // UserA (requester) should have notification
        var notifA = new HttpRequestMessage(HttpMethod.Get, "/api/notifications")
            .WithUser(TestUsers.UserA);
        var notifAResponse = await _client.SendAsync(notifA);
        var notifABody = await notifAResponse.Content.ReadFromJsonAsync<JsonElement>();
        notifABody.GetProperty("data").GetArrayLength().Should().BeGreaterThan(0);

        // UserB (subscriber) should also have notification
        var notifB = new HttpRequestMessage(HttpMethod.Get, "/api/notifications")
            .WithUser(TestUsers.UserB);
        var notifBResponse = await _client.SendAsync(notifB);
        var notifBBody = await notifBResponse.Content.ReadFromJsonAsync<JsonElement>();
        notifBBody.GetProperty("data").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(MediaRequestStatus.Completed)]
    [InlineData(MediaRequestStatus.Rejected)]
    public async Task TerminalStatus_NotificationMessage_ContainsAdminNote(MediaRequestStatus status)
    {
        const string note = "Parsed from the official release";
        var id = await CreateRequest(TestUsers.UserA);
        var deckId = await SeedDeck();

        await SetStatus(id, status, adminNote: note, fulfilledDeckId: status == MediaRequestStatus.Completed ? deckId : null);

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/notifications").WithUser(TestUsers.UserA));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("data").EnumerateArray()
            .Select(n => n.GetProperty("message").GetString())
            .Should().ContainSingle(m => m!.Contains(note));
    }
}
