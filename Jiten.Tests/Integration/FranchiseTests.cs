using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class FranchiseTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedAsync(IEnumerable<Deck> decks, IEnumerable<DeckRelationship> relationships)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        db.DeckRelationships.RemoveRange(db.DeckRelationships);
        db.Decks.RemoveRange(db.Decks);
        await db.SaveChangesAsync();

        db.Decks.AddRange(decks);
        await db.SaveChangesAsync();
        db.DeckRelationships.AddRange(relationships);
        await db.SaveChangesAsync();
    }

    private static Deck Deck(int id, string title = "Deck", MediaType mediaType = MediaType.Anime) =>
        new() { DeckId = id, OriginalTitle = title, MediaType = mediaType, Difficulty = 2.0f };

    private static DeckRelationship Rel(int source, int target, DeckRelationshipType type) =>
        new() { SourceDeckId = source, TargetDeckId = target, RelationshipType = type };

    private async Task<HttpResponseMessage> SendFranchiseAsync(int deckId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/media-deck/{deckId}/franchise");
        // Bypass the server response cache: tests reuse deckId=1, and the cached body would otherwise
        // leak one test's franchise into the next.
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return await _client.SendAsync(request);
    }

    private async Task<FranchiseDto> GetFranchiseAsync(int deckId)
    {
        var response = await SendFranchiseAsync(deckId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<FranchiseDto>();
        dto.Should().NotBeNull();
        return dto!;
    }

    [Fact]
    public async Task Chain_ReturnsFullComponent_FromAnyNode()
    {
        // A -> B -> C (Sequel edges)
        await SeedAsync(
            new[] { Deck(1, "A"), Deck(2, "B"), Deck(3, "C") },
            new[] { Rel(1, 2, DeckRelationshipType.Sequel), Rel(2, 3, DeckRelationshipType.Sequel) });

        foreach (var start in new[] { 1, 2, 3 })
        {
            var dto = await GetFranchiseAsync(start);
            dto.Nodes.Select(n => n.DeckId).Should().BeEquivalentTo(new[] { 1, 2, 3 });
            dto.Edges.Should().HaveCount(2);
            dto.Truncated.Should().BeFalse();
        }
    }

    [Fact]
    public async Task CrossMediaBranch_TraversesAllMediaTypes()
    {
        // Novel(1) -Adaptation-> Anime(2) -Sequel-> Anime2(3); Anime(2) -Spinoff-> Manga(4)
        await SeedAsync(
            new[]
            {
                Deck(1, "Novel", MediaType.Novel),
                Deck(2, "Anime", MediaType.Anime),
                Deck(3, "Anime2", MediaType.Anime),
                Deck(4, "Manga", MediaType.Manga)
            },
            new[]
            {
                Rel(1, 2, DeckRelationshipType.Adaptation),
                Rel(2, 3, DeckRelationshipType.Sequel),
                Rel(2, 4, DeckRelationshipType.Spinoff)
            });

        var dto = await GetFranchiseAsync(3);
        dto.Nodes.Select(n => n.DeckId).Should().BeEquivalentTo(new[] { 1, 2, 3, 4 });
        dto.Edges.Should().HaveCount(3);
        dto.Nodes.Select(n => n.MediaType).Should()
           .Contain(new[] { MediaType.Novel, MediaType.Anime, MediaType.Manga });
        dto.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task UnrelatedDeck_IsNotIncluded()
    {
        // Component {1,2}; deck 3 is unrelated.
        await SeedAsync(
            new[] { Deck(1), Deck(2), Deck(3) },
            new[] { Rel(1, 2, DeckRelationshipType.Sequel) });

        var dto = await GetFranchiseAsync(1);
        dto.Nodes.Select(n => n.DeckId).Should().BeEquivalentTo(new[] { 1, 2 });
        dto.Nodes.Should().NotContain(n => n.DeckId == 3);
    }

    [Fact]
    public async Task Cycle_Terminates_WithoutDuplicates()
    {
        // A -Alternative-> B, B -Alternative-> C, C -Alternative-> A : a cycle.
        await SeedAsync(
            new[] { Deck(1, "A"), Deck(2, "B"), Deck(3, "C") },
            new[]
            {
                Rel(1, 2, DeckRelationshipType.Alternative),
                Rel(2, 3, DeckRelationshipType.Alternative),
                Rel(3, 1, DeckRelationshipType.Alternative)
            });

        var dto = await GetFranchiseAsync(1);
        dto.Nodes.Select(n => n.DeckId).Should().BeEquivalentTo(new[] { 1, 2, 3 });
        dto.Edges.Should().HaveCount(3);
        dto.Nodes.Should().OnlyHaveUniqueItems(n => n.DeckId);
        dto.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Edges_AreDeduplicated()
    {
        // A single edge A->B is reachable from both endpoints during BFS; it must appear once.
        await SeedAsync(
            new[] { Deck(1, "A"), Deck(2, "B") },
            new[] { Rel(1, 2, DeckRelationshipType.Sequel) });

        var dto = await GetFranchiseAsync(1);
        dto.Edges.Should().HaveCount(1);
        var edge = dto.Edges.Single();
        edge.SourceDeckId.Should().Be(1);
        edge.TargetDeckId.Should().Be(2);
        edge.RelationshipType.Should().Be(DeckRelationshipType.Sequel);
    }

    [Fact]
    public async Task NoRelationships_ReturnsSelfNode_EmptyEdges()
    {
        await SeedAsync(new[] { Deck(1, "Lonely") }, Array.Empty<DeckRelationship>());

        var dto = await GetFranchiseAsync(1);
        dto.Nodes.Should().ContainSingle().Which.DeckId.Should().Be(1);
        dto.Edges.Should().BeEmpty();
        dto.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task UnknownDeck_Returns404()
    {
        await SeedAsync(new[] { Deck(1) }, Array.Empty<DeckRelationship>());

        var response = await SendFranchiseAsync(999);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AuthenticatedRequest_DecoratesNodesWithViewerCoverage()
    {
        // A -> B; the viewer has 75% mature / 60% unique coverage on B, none on A.
        await SeedAsync(
            new[] { Deck(1, "A"), Deck(2, "B") },
            new[] { Rel(1, 2, DeckRelationshipType.Sequel) });

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            // Coverage is stored per 1024-deck chunk as basis points (value / 100 = percent).
            var values = new short[1024];
            values[2] = 7500; // deck 2, mature coverage 75.00%
            var uniqueValues = new short[1024];
            uniqueValues[2] = 6000; // deck 2, unique coverage 60.00%
            userDb.UserCoverageChunks.AddRange(
                new UserCoverageChunk { UserId = TestUsers.UserA, Metric = (short)UserCoverageMetric.MatureCoverage, ChunkIndex = 0, Values = values },
                new UserCoverageChunk { UserId = TestUsers.UserA, Metric = (short)UserCoverageMetric.MatureUniqueCoverage, ChunkIndex = 0, Values = uniqueValues });
            await userDb.SaveChangesAsync();
        }

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/media-deck/1/franchise").WithUser(TestUsers.UserA);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<FranchiseDto>();
        dto.Should().NotBeNull();
        var nodeB = dto!.Nodes.Single(n => n.DeckId == 2);
        nodeB.Coverage.Should().BeApproximately(75f, 0.01f);
        nodeB.UniqueCoverage.Should().BeApproximately(60f, 0.01f);
        dto.Nodes.Single(n => n.DeckId == 1).Coverage.Should().Be(0);
    }

    [Fact]
    public async Task ChainLongerThanCap_Truncates_AtCap()
    {
        const int cap = 100;
        const int chainLength = cap + 30;

        var decks = new List<Deck>();
        var rels = new List<DeckRelationship>();
        for (int i = 1; i <= chainLength; i++)
        {
            decks.Add(Deck(i, $"Deck {i}"));
            if (i > 1)
                rels.Add(Rel(i - 1, i, DeckRelationshipType.Sequel));
        }

        await SeedAsync(decks, rels);

        var dto = await GetFranchiseAsync(1);
        dto.Truncated.Should().BeTrue();
        dto.Nodes.Should().HaveCount(cap);
    }
}
