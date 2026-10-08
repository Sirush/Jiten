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

public class DeckStatusCascadeTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(int ParentId, int[] ChildIds)> SeedParentWithChildren(int childCount = 3)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var parent = new Deck { OriginalTitle = "Parent Series", MediaType = MediaType.Anime };
        db.Decks.Add(parent);
        await db.SaveChangesAsync();

        var childIds = new int[childCount];
        for (var i = 0; i < childCount; i++)
        {
            var child = new Deck
            {
                OriginalTitle = $"Episode {i + 1}",
                MediaType = MediaType.Anime,
                ParentDeckId = parent.DeckId,
                DeckOrder = i
            };
            db.Decks.Add(child);
            await db.SaveChangesAsync();
            childIds[i] = child.DeckId;
        }

        return (parent.DeckId, childIds);
    }

    private async Task<JsonElement> SetDeckStatus(int deckId, DeckStatus status, string userId = TestUsers.UserA)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/user/deck-preferences/{deckId}/status")
            .WithUser(userId)
            .WithJsonContent(new { status = (int)status });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<DeckStatus> GetDeckStatus(int deckId, string userId = TestUsers.UserA)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var pref = await db.UserDeckPreferences.FirstOrDefaultAsync(p => p.UserId == userId && p.DeckId == deckId);
        return pref?.Status ?? DeckStatus.None;
    }

    [Fact]
    public async Task MarkChildCompleted_ParentBecomesOngoing()
    {
        var (parentId, childIds) = await SeedParentWithChildren();

        var body = await SetDeckStatus(childIds[0], DeckStatus.Completed);

        var parentStatus = await GetDeckStatus(parentId);
        parentStatus.Should().Be(DeckStatus.Ongoing);

        body.GetProperty("parentDeckId").GetInt32().Should().Be(parentId);
        body.GetProperty("parentStatus").GetInt32().Should().Be((int)DeckStatus.Ongoing);
    }

    [Fact]
    public async Task MarkChildOngoing_ParentBecomesOngoing()
    {
        var (parentId, childIds) = await SeedParentWithChildren();

        var body = await SetDeckStatus(childIds[0], DeckStatus.Ongoing);

        var parentStatus = await GetDeckStatus(parentId);
        parentStatus.Should().Be(DeckStatus.Ongoing);
    }

    [Fact]
    public async Task MarkChildDropped_ParentBecomesOngoing()
    {
        var (parentId, childIds) = await SeedParentWithChildren();

        var body = await SetDeckStatus(childIds[0], DeckStatus.Dropped);

        var parentStatus = await GetDeckStatus(parentId);
        parentStatus.Should().Be(DeckStatus.Ongoing);
    }

    [Fact]
    public async Task MarkAllChildrenCompleted_ParentStaysOngoing_ResponseSignalsAllCompleted()
    {
        var (parentId, childIds) = await SeedParentWithChildren();

        JsonElement body = default;
        foreach (var childId in childIds)
            body = await SetDeckStatus(childId, DeckStatus.Completed);

        var parentStatus = await GetDeckStatus(parentId);
        parentStatus.Should().Be(DeckStatus.Ongoing);
        body.GetProperty("allChildrenCompleted").GetBoolean().Should().BeTrue();
        body.GetProperty("parentDeckId").GetInt32().Should().Be(parentId);
    }

    [Fact]
    public async Task LastChildCompleted_ResponseIncludesAllChildrenCompleted()
    {
        var (parentId, childIds) = await SeedParentWithChildren(2);

        await SetDeckStatus(childIds[0], DeckStatus.Completed);
        var body = await SetDeckStatus(childIds[1], DeckStatus.Completed);

        body.GetProperty("parentDeckId").GetInt32().Should().Be(parentId);
        body.GetProperty("allChildrenCompleted").GetBoolean().Should().BeTrue();
        var parentStatus = await GetDeckStatus(parentId);
        parentStatus.Should().Be(DeckStatus.Ongoing);
    }

    [Fact]
    public async Task OneChildDropped_OthersCompleted_ParentStaysOngoing()
    {
        var (parentId, childIds) = await SeedParentWithChildren(3);

        await SetDeckStatus(childIds[0], DeckStatus.Completed);
        await SetDeckStatus(childIds[1], DeckStatus.Completed);
        await SetDeckStatus(childIds[2], DeckStatus.Dropped);

        var parentStatus = await GetDeckStatus(parentId);
        parentStatus.Should().Be(DeckStatus.Ongoing);
    }

    [Fact]
    public async Task ParentAlreadyOngoing_StaysOngoing_WhenChildCompleted()
    {
        var (parentId, childIds) = await SeedParentWithChildren();

        await SetDeckStatus(childIds[0], DeckStatus.Ongoing);
        var body = await SetDeckStatus(childIds[1], DeckStatus.Completed);

        var parentStatus = await GetDeckStatus(parentId);
        parentStatus.Should().Be(DeckStatus.Ongoing);

        body.GetProperty("parentDeckId").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("parentStatus").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ParentManuallyCompleted_NotDowngraded_WhenChildSetToOngoing()
    {
        var (parentId, childIds) = await SeedParentWithChildren();

        await SetDeckStatus(parentId, DeckStatus.Completed);
        await SetDeckStatus(childIds[0], DeckStatus.Ongoing);

        var parentStatus = await GetDeckStatus(parentId);
        parentStatus.Should().Be(DeckStatus.Completed);
    }

    [Fact]
    public async Task ParentManuallyDropped_NotChanged_WhenChildCompleted()
    {
        var (parentId, childIds) = await SeedParentWithChildren();

        await SetDeckStatus(parentId, DeckStatus.Dropped);
        await SetDeckStatus(childIds[0], DeckStatus.Completed);

        var parentStatus = await GetDeckStatus(parentId);
        parentStatus.Should().Be(DeckStatus.Dropped);
    }

    [Fact]
    public async Task ParentManuallyDropped_NotChanged_WhenAllChildrenCompleted()
    {
        var (parentId, childIds) = await SeedParentWithChildren(2);

        await SetDeckStatus(parentId, DeckStatus.Dropped);
        await SetDeckStatus(childIds[0], DeckStatus.Completed);
        var body = await SetDeckStatus(childIds[1], DeckStatus.Completed);

        var parentStatus = await GetDeckStatus(parentId);
        parentStatus.Should().Be(DeckStatus.Dropped);
        body.GetProperty("allChildrenCompleted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ChildPlanning_DoesNotAffectParent()
    {
        var (parentId, childIds) = await SeedParentWithChildren();

        var body = await SetDeckStatus(childIds[0], DeckStatus.Planning);

        var parentStatus = await GetDeckStatus(parentId);
        parentStatus.Should().Be(DeckStatus.None);

        body.GetProperty("parentDeckId").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task DeckWithoutParent_NoParentInResponse()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var deck = new Deck { OriginalTitle = "Standalone", MediaType = MediaType.Anime };
        db.Decks.Add(deck);
        await db.SaveChangesAsync();

        var body = await SetDeckStatus(deck.DeckId, DeckStatus.Completed);

        body.GetProperty("parentDeckId").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("parentStatus").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("allChildrenCompleted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DifferentUsers_IndependentCascade()
    {
        var (parentId, childIds) = await SeedParentWithChildren(2);

        await SetDeckStatus(childIds[0], DeckStatus.Completed, TestUsers.UserA);
        var bodyA = await SetDeckStatus(childIds[1], DeckStatus.Completed, TestUsers.UserA);

        var bodyB = await SetDeckStatus(childIds[0], DeckStatus.Completed, TestUsers.UserB);

        var parentStatusA = await GetDeckStatus(parentId, TestUsers.UserA);
        var parentStatusB = await GetDeckStatus(parentId, TestUsers.UserB);

        parentStatusA.Should().Be(DeckStatus.Ongoing);
        bodyA.GetProperty("allChildrenCompleted").GetBoolean().Should().BeTrue();
        parentStatusB.Should().Be(DeckStatus.Ongoing);
        bodyB.GetProperty("allChildrenCompleted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PausingAVolume_PausesASeriesWithNoStatus()
    {
        var (parentId, childIds) = await SeedParentWithChildren();

        await SetDeckStatus(childIds[0], DeckStatus.Paused);

        (await GetDeckStatus(parentId)).Should().Be(DeckStatus.Paused);
    }

    [Fact]
    public async Task PausedSeries_ResumesWhenAVolumeIsRead_ButNotWhenOneIsDropped()
    {
        var (parentId, childIds) = await SeedParentWithChildren();
        await SetDeckStatus(parentId, DeckStatus.Paused);

        await SetDeckStatus(childIds[0], DeckStatus.Dropped);
        (await GetDeckStatus(parentId)).Should().Be(DeckStatus.Paused);

        await SetDeckStatus(childIds[1], DeckStatus.Completed);
        (await GetDeckStatus(parentId)).Should().Be(DeckStatus.Ongoing);
    }
}
