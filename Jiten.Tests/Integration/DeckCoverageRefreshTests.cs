using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Controllers;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.FSRS;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// Per-media coverage refresh. The compute itself is raw Postgres and cannot run on the SQLite test
/// host, so these tests pin the surrounding contract: auth, deck resolution and the gate responses.
/// </summary>
public class DeckCoverageRefreshTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.UserWordSetStates.ExecuteDeleteAsync();
        await userDb.UserCoverageChunks.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(int RootId, int ChildId)> SeedSeries()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var parent = new Deck { OriginalTitle = "Coverage Series", MediaType = MediaType.Anime, DifficultyOverride = -1 };
        db.Decks.Add(parent);
        await db.SaveChangesAsync();

        var children = Enumerable.Range(1, 3)
                                 .Select(i => new Deck
                                 {
                                     OriginalTitle = $"第{i}話", MediaType = MediaType.Anime,
                                     ParentDeckId = parent.DeckId, DeckOrder = i, DifficultyOverride = -1,
                                 })
                                 .ToList();
        db.Decks.AddRange(children);
        await db.SaveChangesAsync();

        return (parent.DeckId, children[0].DeckId);
    }

    private async Task SeedEligibleUser(string userId = TestUsers.UserA)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        for (var i = 1; i <= 10; i++)
        {
            userDb.FsrsCards.Add(new FsrsCard
            {
                CardId = i, UserId = userId, WordId = i, ReadingIndex = 0,
                State = FsrsState.Review, Due = DateTime.UtcNow.AddDays(30),
                LastReview = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow.AddDays(-40),
            });
        }

        await userDb.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> Refresh(int deckId, bool authenticated = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/media-deck/{deckId}/coverage/refresh");
        if (authenticated) request.WithUser(TestUsers.UserA);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task AnonymousCall_Returns401()
    {
        var (rootId, _) = await SeedSeries();

        var response = await Refresh(rootId, authenticated: false);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnknownDeck_Returns404()
    {
        var response = await Refresh(999_999);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UserBelowEligibilityThreshold_GetsNotEligible()
    {
        var (rootId, _) = await SeedSeries();

        var response = await Refresh(rootId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("not_eligible");
    }

    [Fact]
    public async Task EligibleUserWithoutBaseline_GetsNoBaseline()
    {
        var (rootId, _) = await SeedSeries();
        await SeedEligibleUser();

        var response = await Refresh(rootId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("no_baseline");
    }

    [Fact]
    public async Task ResolveMediaDeckIds_FromRoot_ReturnsRootAndAllChildren()
    {
        var (rootId, _) = await SeedSeries();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var ids = await MediaDeckController.ResolveMediaDeckIdsAsync(db, rootId);

        ids.Should().HaveCount(4).And.Contain(rootId);
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task ResolveMediaDeckIds_FromChild_ReturnsRootAndAllSiblings()
    {
        var (rootId, childId) = await SeedSeries();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var ids = await MediaDeckController.ResolveMediaDeckIdsAsync(db, childId);

        ids.Should().HaveCount(4).And.Contain([rootId, childId]);
    }

    [Fact]
    public async Task ResolveMediaDeckIds_ChildlessDeck_ReturnsJustItself()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var deck = new Deck { OriginalTitle = "Standalone", MediaType = MediaType.Novel, DifficultyOverride = -1 };
        db.Decks.Add(deck);
        await db.SaveChangesAsync();

        var ids = await MediaDeckController.ResolveMediaDeckIdsAsync(db, deck.DeckId);

        ids.Should().Equal(deck.DeckId);
    }

    [Fact]
    public async Task ResolveMediaDeckIds_UnknownDeck_ReturnsNull()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        var ids = await MediaDeckController.ResolveMediaDeckIdsAsync(db, 999_999);

        ids.Should().BeNull();
    }
}
