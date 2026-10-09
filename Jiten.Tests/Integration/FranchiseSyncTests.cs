using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class FranchiseSyncTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await db.DeckRelationships.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Deck Deck(int id, string title, int year = 2000) =>
        new() { DeckId = id, OriginalTitle = title, MediaType = MediaType.Anime, ReleaseDate = new DateOnly(year, 1, 1) };

    private static DeckRelationship Rel(int source, int target, DeckRelationshipType type = DeckRelationshipType.Sequel) =>
        new() { SourceDeckId = source, TargetDeckId = target, RelationshipType = type };

    private static Series Group(int id, string name, SeriesKind kind, params int[] deckIds)
    {
        var series = new Series { SeriesId = id, Name = name, Kind = kind };
        foreach (var deckId in deckIds)
            series.Members.Add(new SeriesMember { SeriesId = id, DeckId = deckId });
        return series;
    }

    private async Task SeedAsync(IEnumerable<Deck> decks, IEnumerable<DeckRelationship>? relationships = null,
                                 IEnumerable<Series>? series = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        db.Decks.AddRange(decks);
        await db.SaveChangesAsync();
        if (relationships != null)
            db.DeckRelationships.AddRange(relationships);
        if (series != null)
            db.Series.AddRange(series);
        await db.SaveChangesAsync();
    }

    private async Task<T> InDbAsync<T>(Func<JitenDbContext, Task<T>> action)
    {
        using var scope = factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<JitenDbContext>());
    }

    private Task InDbAsync(Func<JitenDbContext, Task> action) => InDbAsync(async db =>
    {
        await action(db);
        return 0;
    });

    private async Task<FranchiseSyncSummary> SyncAsync()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/admin/franchise/sync").WithAdmin());
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<FranchiseSyncSummary>())!;
    }

    private Task<Dictionary<int, int?>> MembershipAsync() =>
        InDbAsync(db => db.Decks.AsNoTracking().ToDictionaryAsync(d => d.DeckId, d => d.FranchiseId));

    private Task<List<Franchise>> FranchisesAsync() =>
        InDbAsync(db => db.Franchises.AsNoTracking().OrderBy(f => f.FranchiseId).ToListAsync());

    private async Task<T> GetAsync<T>(string url, bool admin = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        if (admin)
            request.WithAdmin();
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task Components_JoinLinksAndSeries_SingleDecksStayNull()
    {
        // Story line 1-2, line 3-4 and deck 5 share a series; 6 and 7 share only a setting; 8 is alone.
        await SeedAsync([Deck(1, "A"), Deck(2, "B"), Deck(3, "C"), Deck(4, "D"), Deck(5, "E"), Deck(6, "F"), Deck(7, "G"), Deck(8, "H"),
                         Deck(9, "I"), Deck(10, "J")],
                        [Rel(1, 2), Rel(3, 4, DeckRelationshipType.Adaptation), Rel(9, 10, DeckRelationshipType.SameSeries)],
                        [Group(1, "Saga", SeriesKind.Series, 2, 3, 5), Group(3, "World", SeriesKind.Setting, 6, 7)]);

        var summary = await SyncAsync();

        summary.Created.Should().Be(1);
        var membership = await MembershipAsync();
        var franchiseId = membership[1];
        franchiseId.Should().NotBeNull();
        new[] { 1, 2, 3, 4, 5 }.Select(id => membership[id]).Should().AllBeEquivalentTo(franchiseId);
        new[] { 6, 7, 8, 9, 10 }.Select(id => membership[id]).Should().AllBeEquivalentTo((int?)null);
        (await FranchisesAsync()).Single().Name.Should().Be("Saga");
    }

    [Fact]
    public async Task SecondRun_ChangesNothing()
    {
        await SeedAsync([Deck(1, "Alpha One"), Deck(2, "Alpha Two")], [Rel(1, 2)]);

        await SyncAsync();
        var again = await SyncAsync();

        again.Should().Be(new FranchiseSyncSummary(0, 0, 0, 0, 1, 0));
    }

    [Fact]
    public async Task Merge_KeepsTheMajorityId_AndRepointsTheOther()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B"), Deck(3, "C"), Deck(4, "D"), Deck(5, "E")],
                        [Rel(1, 2), Rel(2, 3), Rel(4, 5)]);
        await SyncAsync();
        var before = await MembershipAsync();
        var big = before[1]!.Value;
        var small = before[4]!.Value;

        await SeedAsync([], [Rel(3, 4)]);
        var repoints = new List<(int OldId, int NewId)>();
        var summary = await InDbAsync(db => FranchiseSync.RunAsync(db, (oldId, newId) =>
        {
            repoints.Add((oldId, newId));
            return Task.CompletedTask;
        }));

        summary.Merged.Should().Be(1);
        summary.Deleted.Should().Be(0);
        summary.DecksUpdated.Should().Be(2);
        repoints.Should().Equal((small, big));
        (await MembershipAsync()).Values.Should().AllBeEquivalentTo((int?)big);
        (await FranchisesAsync()).Select(f => f.FranchiseId).Should().Equal(big);
    }

    [Fact]
    public async Task Merge_OfEqualHalves_KeepsTheLowestId()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B"), Deck(3, "C"), Deck(4, "D")], [Rel(1, 2), Rel(3, 4)]);
        await SyncAsync();
        var ids = (await FranchisesAsync()).Select(f => f.FranchiseId).ToList();

        await SeedAsync([], [Rel(2, 3)]);
        await SyncAsync();

        (await FranchisesAsync()).Select(f => f.FranchiseId).Should().Equal(ids.Min());
    }

    [Fact]
    public async Task Split_LargerPartKeepsTheId_TheOtherGetsANewRow_LoneDeckIsCleared()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B"), Deck(3, "C"), Deck(4, "D"), Deck(5, "E"), Deck(6, "F")],
                        [Rel(1, 2), Rel(2, 3), Rel(3, 4), Rel(4, 5), Rel(5, 6)]);
        await SyncAsync();
        var original = (await MembershipAsync())[1]!.Value;

        await InDbAsync(db => db.DeckRelationships.Where(r => (r.SourceDeckId == 3 && r.TargetDeckId == 4) ||
                                                              (r.SourceDeckId == 5 && r.TargetDeckId == 6))
                                .ExecuteDeleteAsync());
        var summary = await SyncAsync();

        summary.Created.Should().Be(1);
        var membership = await MembershipAsync();
        new[] { 1, 2, 3 }.Select(id => membership[id]).Should().AllBeEquivalentTo((int?)original);
        membership[4].Should().NotBeNull().And.NotBe(original);
        membership[5].Should().Be(membership[4]);
        membership[6].Should().BeNull();
    }

    [Fact]
    public async Task LostFranchise_IsDeleted_WithoutRepoint()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B")], [Rel(1, 2)]);
        await SyncAsync();

        await InDbAsync(db => db.DeckRelationships.ExecuteDeleteAsync());
        var repointed = false;
        var summary = await InDbAsync(db => FranchiseSync.RunAsync(db, (_, _) =>
        {
            repointed = true;
            return Task.CompletedTask;
        }));

        summary.Deleted.Should().Be(1);
        summary.Merged.Should().Be(0);
        repointed.Should().BeFalse();
        (await FranchisesAsync()).Should().BeEmpty();
        (await MembershipAsync()).Values.Should().AllBeEquivalentTo((int?)null);
    }

    [Fact]
    public async Task Naming_UsesTheSharedPrefix_AndFollowsTitleChanges()
    {
        await SeedAsync([Deck(1, "ドラゴンクエストⅢ", 1988), Deck(2, "ドラゴンクエストⅣ", 1990), Deck(3, "スライムもりもり", 2003)],
                        [Rel(1, 2), Rel(2, 3, DeckRelationshipType.Spinoff)]);
        await SyncAsync();
        (await FranchisesAsync()).Single().Name.Should().Be("ドラゴンクエスト");

        await InDbAsync(db => db.Decks.Where(d => d.DeckId == 2).ExecuteUpdateAsync(s => s.SetProperty(d => d.OriginalTitle, "勇者の物語")));
        var summary = await SyncAsync();

        summary.Renamed.Should().Be(1);
        (await FranchisesAsync()).Single().Name.Should().Be("ドラゴンクエストⅢ");
    }

    [Fact]
    public async Task ManualName_SurvivesSyncs_AndClearingItRenamesAtOnce()
    {
        await SeedAsync([Deck(1, "Alpha One"), Deck(2, "Alpha Two")], [Rel(1, 2)]);
        await SyncAsync();
        var id = (await FranchisesAsync()).Single().FranchiseId;

        var renamed = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/admin/franchise/{id}")
                                              .WithAdmin().WithJsonContent(new { name = "  The Alpha Saga " }));
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await renamed.Content.ReadFromJsonAsync<FranchiseSummaryDto>())!.Should()
            .BeEquivalentTo(new { FranchiseId = id, Name = "The Alpha Saga", NameIsManual = true, DeckCount = 2 });

        (await SyncAsync()).Renamed.Should().Be(0);
        (await FranchisesAsync()).Single().Name.Should().Be("The Alpha Saga");

        var reset = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, $"/api/admin/franchise/{id}")
                                            .WithAdmin().WithJsonContent(new { name = (string?)null }));
        reset.StatusCode.Should().Be(HttpStatusCode.OK);
        (await reset.Content.ReadFromJsonAsync<FranchiseSummaryDto>())!.Should()
            .BeEquivalentTo(new { Name = "Alpha", NameIsManual = false });

        (await _client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/api/admin/franchise/999")
                                 .WithAdmin().WithJsonContent(new { name = "x" })))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AdminList_FiltersAndCounts()
    {
        await SeedAsync([Deck(1, "Alpha One"), Deck(2, "Alpha Two"), Deck(3, "Alpha Three"), Deck(4, "Beta One"), Deck(5, "Beta Two")],
                        [Rel(1, 2), Rel(4, 5)],
                        [Group(1, "Alpha", SeriesKind.Series, 2, 3), Group(2, "Place", SeriesKind.Setting, 1, 4)]);
        await SyncAsync();

        var all = await GetAsync<PaginatedResponse<List<FranchiseSummaryDto>>>("/api/admin/franchise", admin: true);
        all.TotalItems.Should().Be(2);

        var alpha = await GetAsync<PaginatedResponse<List<FranchiseSummaryDto>>>("/api/admin/franchise?query=alp", admin: true);
        alpha.Data.Should().ContainSingle().Which.Should()
             .BeEquivalentTo(new { Name = "Alpha", NameIsManual = false, DeckCount = 3, SeriesCount = 1, FirstDeckId = 1, NameTitles = (object?)null });

        var byDeckTitle = await GetAsync<PaginatedResponse<List<FranchiseSummaryDto>>>("/api/admin/franchise?query=beta%20two", admin: true);
        byDeckTitle.Data.Should().ContainSingle().Which.DeckCount.Should().Be(2);

        var manualOnly = await GetAsync<PaginatedResponse<List<FranchiseSummaryDto>>>("/api/admin/franchise?manual=true", admin: true);
        manualOnly.TotalItems.Should().Be(0);
    }

    [Fact]
    public async Task AdminList_SortsByDisplayNameIgnoringLeadingPunctuation()
    {
        var oshi = Deck(1, "【推しの子】");
        oshi.EnglishTitle = "Oshi no Ko";
        await SeedAsync([oshi, Deck(2, "Other"), Deck(3, "“Bungaku Shoujo”"), Deck(4, "Other 2"), Deck(5, "Zeta"), Deck(6, "Zeta 2"), Deck(7, "Alpha"), Deck(8, "Alpha 2")],
                        [Rel(1, 2), Rel(3, 4), Rel(5, 6), Rel(7, 8)]);
        await SyncAsync();

        var original = await GetAsync<PaginatedResponse<List<FranchiseSummaryDto>>>("/api/admin/franchise", admin: true);
        original.Data.Select(f => f.Name).Should().Equal("Alpha", "“Bungaku Shoujo”", "Zeta", "【推しの子】");
        original.Data.Last().NameTitles!.EnglishTitle.Should().Be("Oshi no Ko");

        var english = await GetAsync<PaginatedResponse<List<FranchiseSummaryDto>>>("/api/admin/franchise?titleLanguage=2&descending=true", admin: true);
        english.Data.Select(f => f.Name).Should().Equal("Zeta", "【推しの子】", "“Bungaku Shoujo”", "Alpha");

        var byDecks = await GetAsync<PaginatedResponse<List<FranchiseSummaryDto>>>("/api/admin/franchise?sort=decks&descending=true&limit=1", admin: true);
        byDecks.TotalItems.Should().Be(4);
        byDecks.Data.Should().ContainSingle();
    }

    [Fact]
    public async Task SeriesMutations_RunTheSync()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B")], series: [Group(1, "Saga", SeriesKind.Series)]);

        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/admin/franchise-builder/save").WithAdmin().WithJsonContent(new
        {
            anchorDeckId = 1,
            addEdges = Array.Empty<object>(), removeEdges = Array.Empty<object>(), removeMembers = Array.Empty<object>(),
            addMembers = new[] { new { seriesId = 1, deckId = 1 }, new { seriesId = 1, deckId = 2 } }
        }));
        (await MembershipAsync()).Values.Should().AllBeEquivalentTo((await FranchisesAsync()).Single().FranchiseId);

        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/admin/series/1/members/remove")
                                .WithAdmin().WithJsonContent(new { deckIds = new[] { 2 } }));
        (await FranchisesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DeckMetadataPatch_WithNewLink_RunsTheSync()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B")]);

        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/api/admin/deck/1/metadata").WithAdmin()
                                                   .WithJsonContent(new { relationships = new[] { new { sourceDeckId = 1, targetDeckId = 2, relationshipType = 1 } } }));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var membership = await MembershipAsync();
        membership[1].Should().NotBeNull();
        membership[2].Should().Be(membership[1]);
    }

    [Fact]
    public async Task DeckDetail_CarriesTheFranchise()
    {
        await SeedAsync([Deck(1, "Alpha One"), Deck(2, "Alpha Two"), Deck(3, "Alone")], [Rel(1, 2)]);
        await SyncAsync();

        var detail = await GetAsync<PaginatedResponse<DeckDetailDto>>("/api/media-deck/1/detail");
        detail.Data.MainDeck.FranchiseId.Should().Be((await FranchisesAsync()).Single().FranchiseId);

        var alone = await GetAsync<PaginatedResponse<DeckDetailDto>>("/api/media-deck/3/detail");
        alone.Data.MainDeck.FranchiseId.Should().BeNull();
    }
}
