using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.Providers;
using Jiten.Core.Data.User;
using Jiten.Core.Services;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class SeriesTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        await db.DeckWords.ExecuteDeleteAsync();
        await db.DeckRelationships.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static Deck Deck(int id, string title, MediaType mediaType = MediaType.Anime, int year = 2000, int chars = 1000,
                             float difficulty = 2f) =>
        new()
        {
            DeckId = id, OriginalTitle = title, MediaType = mediaType, ReleaseDate = new DateOnly(year, 1, 1),
            CharacterCount = chars, Difficulty = difficulty, DifficultyOverride = -1
        };

    private static DeckRelationship Rel(int source, int target, DeckRelationshipType type) =>
        new() { SourceDeckId = source, TargetDeckId = target, RelationshipType = type };

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

    private static Series Group(int id, string name, SeriesKind kind, params int[] deckIds)
    {
        var series = new Series { SeriesId = id, OriginalTitle = name, Kind = kind };
        foreach (var deckId in deckIds)
            series.Members.Add(new SeriesMember { SeriesId = id, DeckId = deckId });
        return series;
    }

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

    private Task<HttpResponseMessage> AdminSendAsync(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url).WithAdmin();
        if (body != null)
            request.WithJsonContent(body);
        return _client.SendAsync(request);
    }

    private async Task<FranchiseDto> FranchiseAsync(int deckId)
    {
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<FranchiseSyncRunner>().RunAsync();
        return await GetAsync<FranchiseDto>($"/api/admin/franchise-builder/{deckId}", admin: true);
    }

    [Fact]
    public async Task Franchise_SeriesMembership_AdmitsWholeSeries()
    {
        // 1 -Sequel-> 2; 3 stands alone but shares a series with 2.
        await SeedAsync([Deck(1, "A"), Deck(2, "B"), Deck(3, "C"), Deck(4, "Unrelated")],
                        [Rel(1, 2, DeckRelationshipType.Sequel)],
                        [Group(1, "Saga", SeriesKind.Series, 2, 3)]);

        var dto = await FranchiseAsync(1);

        dto.Nodes.Select(n => n.DeckId).Should().BeEquivalentTo([1, 2, 3]);
        dto.Series.Should().ContainSingle().Which.MemberDeckIds.Should().BeEquivalentTo([2, 3]);
        dto.PreferredView.Should().Be("series");
    }

    [Fact]
    public async Task Franchise_DeckInTwoSeries_JoinsBoth()
    {
        await SeedAsync([Deck(10, "Root deck"), Deck(11, "Shared deck"), Deck(12, "Other deck")],
                        series: [Group(1, "Final Fantasy", SeriesKind.Series, 10, 11), Group(2, "Compilation", SeriesKind.Series, 11, 12)]);

        var dto = await FranchiseAsync(12);

        dto.Nodes.Select(n => n.DeckId).Should().BeEquivalentTo([10, 11, 12]);
        dto.Series.Select(s => s.SeriesId).Should().BeEquivalentTo([1, 2]);
        dto.Series.Single(s => s.SeriesId == 1).MemberDeckIds.Should().BeEquivalentTo([10, 11]);
    }

    [Fact]
    public async Task Franchise_Settings_DoNotMerge_AndListOutsideMembers()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B"), Deck(50, "Elsewhere 1", year: 1990), Deck(51, "Elsewhere 2", year: 1995)],
                        [Rel(1, 2, DeckRelationshipType.Sequel)],
                        [Group(1, "Ivalice", SeriesKind.Setting, 1, 50, 51)]);

        var dto = await FranchiseAsync(2);

        dto.Nodes.Select(n => n.DeckId).Should().BeEquivalentTo([1, 2]);
        dto.Series.Should().BeEmpty();
        var setting = dto.Settings.Should().ContainSingle().Subject;
        setting.MemberDeckIds.Should().BeEquivalentTo([1]);
        setting.OutsideCount.Should().Be(2);
        setting.Outside.Select(n => n.DeckId).Should().Equal(50, 51);
        dto.PreferredView.Should().Be("timeline");
    }

    [Fact]
    public async Task Franchise_SeriesWithOneStoryLine_PrefersTimeline()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B")],
                        [Rel(1, 2, DeckRelationshipType.Sequel)],
                        [Group(1, "Saga", SeriesKind.Series, 1, 2)]);

        (await FranchiseAsync(1)).PreferredView.Should().Be("timeline");
    }

    [Fact]
    public async Task Franchise_IgnoresLegacyGroupEdges()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B")], [Rel(1, 2, DeckRelationshipType.SameSeries)]);

        var dto = await FranchiseAsync(1);

        dto.Nodes.Select(n => n.DeckId).Should().Equal(1);
        dto.Edges.Should().BeEmpty();
    }

    [Fact]
    public async Task SeriesDetail_ListsItsOwnMembers()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B")],
                        series: [Group(1, "Final Fantasy", SeriesKind.Series, 1), Group(2, "Compilation", SeriesKind.Series, 2)]);

        var detail = await GetAsync<SeriesDetailDto>("/api/admin/series/1", admin: true);

        detail.OriginalTitle.Should().Be("Final Fantasy");
        detail.Kind.Should().Be(SeriesKind.Series);
        detail.Members.Select(m => m.DeckId).Should().Equal(1);
        detail.FranchiseId.Should().BeNull();
    }

    [Fact]
    public async Task DeckDetail_ListsItsSeriesAndSettings()
    {
        await SeedAsync([Deck(1, "A")],
                        series: [Group(1, "Final Fantasy", SeriesKind.Series), Group(2, "Compilation", SeriesKind.Series, 1),
                                 Group(3, "Gaia", SeriesKind.Setting, 1)]);

        var detail = await GetAsync<PaginatedResponse<DeckDetailDto>>("/api/media-deck/1/detail");

        detail.Data.MainDeck.Series.Select(s => (s.SeriesId, s.OriginalTitle, s.Kind))
              .Should().Equal((2, "Compilation", SeriesKind.Series), (3, "Gaia", SeriesKind.Setting));
    }

    [Fact]
    public async Task MediaList_ListsDirectSeriesOfEachDeck()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B")], series: [Group(1, "Final Fantasy", SeriesKind.Series, 1)]);

        var list = await GetAsync<PaginatedResponse<List<DeckDto>>>("/api/media-deck/get-media-decks");

        list.Data.Single(d => d.DeckId == 1).Series.Should().ContainSingle().Which.OriginalTitle.Should().Be("Final Fantasy");
        list.Data.Single(d => d.DeckId == 2).Series.Should().BeEmpty();
    }

    [Fact]
    public async Task AdminSeries_CreateRenameAndDelete()
    {
        var created = await AdminSendAsync(HttpMethod.Post, "/api/admin/series",
                                           new { originalTitle = " サーガ ", romajiTitle = "Saaga", englishTitle = " ", kind = 1 });
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var saga = (await created.Content.ReadFromJsonAsync<SeriesRefDto>())!;
        saga.Should().BeEquivalentTo(new { OriginalTitle = "サーガ", RomajiTitle = "Saaga", EnglishTitle = (string?)null, Kind = SeriesKind.Series });

        (await AdminSendAsync(HttpMethod.Post, "/api/admin/series", new { originalTitle = "World", kind = 2 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AdminSendAsync(HttpMethod.Post, "/api/admin/series", new { originalTitle = "Bad", kind = 9 })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AdminSendAsync(HttpMethod.Post, "/api/admin/series", new { originalTitle = " ", englishTitle = "Saga", kind = 1 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var untouched = await AdminSendAsync(HttpMethod.Patch, $"/api/admin/series/{saga.SeriesId}", new { });
        (await untouched.Content.ReadFromJsonAsync<SeriesRefDto>())!.RomajiTitle.Should().Be("Saaga");

        var renamed = await AdminSendAsync(HttpMethod.Patch, $"/api/admin/series/{saga.SeriesId}", new { originalTitle = "Renamed", englishTitle = "Renamed EN" });
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await renamed.Content.ReadFromJsonAsync<SeriesRefDto>())!.Should()
            .BeEquivalentTo(new { OriginalTitle = "Renamed", RomajiTitle = (string?)null, EnglishTitle = "Renamed EN" });

        var search = await GetAsync<PaginatedResponse<List<SeriesSummaryDto>>>("/api/admin/series?query=renamed%20en", admin: true);
        search.Data.Should().ContainSingle().Which.SeriesId.Should().Be(saga.SeriesId);

        (await AdminSendAsync(HttpMethod.Delete, $"/api/admin/series/{saga.SeriesId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await AdminSendAsync(HttpMethod.Delete, $"/api/admin/series/{saga.SeriesId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AdminSeries_MembershipRemove()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B")], series: [Group(1, "Saga", SeriesKind.Series, 1, 2)]);

        var removed = await AdminSendAsync(HttpMethod.Post, "/api/admin/series/1/members/remove", new { deckIds = new[] { 2, 2 } });
        (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("removed").GetInt32().Should().Be(1);
        (await AdminSendAsync(HttpMethod.Post, "/api/admin/series/1/members", new { deckIds = new[] { 2 } }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var list = await GetAsync<PaginatedResponse<List<SeriesSummaryDto>>>("/api/admin/series?query=sag", admin: true);
        list.Data.Should().ContainSingle().Which.DeckCount.Should().Be(1);
    }

    [Fact]
    public async Task AdminSeries_MergeMovesMembers()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B"), Deck(3, "C")],
                        series: [Group(1, "Source", SeriesKind.Series, 1, 2), Group(2, "Target", SeriesKind.Series, 2, 3),
                                 Group(3, "World", SeriesKind.Setting, 3)]);
        int seriesDeckId, franchiseDeckId;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var onSeries = GroupStudyDeck(MediaGroupKind.Series, 1);
            var onFranchise = GroupStudyDeck(MediaGroupKind.Franchise, 1);
            userDb.UserStudyDecks.AddRange(onSeries, onFranchise);
            await userDb.SaveChangesAsync();
            (seriesDeckId, franchiseDeckId) = (onSeries.UserStudyDeckId, onFranchise.UserStudyDeckId);
        }

        (await AdminSendAsync(HttpMethod.Post, "/api/admin/series/1/merge-into/1"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AdminSendAsync(HttpMethod.Post, "/api/admin/series/1/merge-into/3"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AdminSendAsync(HttpMethod.Post, "/api/admin/series/1/merge-into/2"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var target = await GetAsync<SeriesDetailDto>("/api/admin/series/2", admin: true);
        target.Members.Select(m => m.DeckId).Should().BeEquivalentTo([1, 2, 3]);
        var gone = new HttpRequestMessage(HttpMethod.Get, "/api/admin/series/1").WithAdmin();
        gone.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        (await _client.SendAsync(gone)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var check = factory.Services.CreateScope();
        var groups = await check.ServiceProvider.GetRequiredService<UserDbContext>().UserStudyDecks.AsNoTracking()
                                .ToDictionaryAsync(sd => sd.UserStudyDeckId, sd => sd.GroupId);
        groups[seriesDeckId].Should().Be(2);
        groups[franchiseDeckId].Should().Be(1);
    }

    [Fact]
    public async Task AdminSeries_DeleteMovesStudyDecksToTheHomeFranchise()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B")], [Rel(2, 1, DeckRelationshipType.Sequel)],
                        [Group(1, "Saga", SeriesKind.Series, 1), Group(2, "Empty", SeriesKind.Series), Group(3, "World", SeriesKind.Setting, 1)]);
        var franchiseId = (await FranchiseAsync(1)).FranchiseId!.Value;

        int onSeries, onEmpty, onSetting;
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var series = GroupStudyDeck(MediaGroupKind.Series, 1);
            var empty = GroupStudyDeck(MediaGroupKind.Series, 2);
            var setting = GroupStudyDeck(MediaGroupKind.Series, 3);
            userDb.UserStudyDecks.AddRange(series, empty, setting);
            await userDb.SaveChangesAsync();
            (onSeries, onEmpty, onSetting) = (series.UserStudyDeckId, empty.UserStudyDeckId, setting.UserStudyDeckId);
        }

        async Task<(MediaGroupKind? Kind, int? Id)> GroupOf(int studyDeckId)
        {
            using var scope = factory.Services.CreateScope();
            var sd = await scope.ServiceProvider.GetRequiredService<UserDbContext>().UserStudyDecks.AsNoTracking()
                                .SingleAsync(d => d.UserStudyDeckId == studyDeckId);
            return (sd.GroupKind, sd.GroupId);
        }

        (await AdminSendAsync(HttpMethod.Delete, "/api/admin/series/1")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GroupOf(onSeries)).Should().Be(((MediaGroupKind?)MediaGroupKind.Franchise, (int?)franchiseId));

        (await AdminSendAsync(HttpMethod.Delete, "/api/admin/series/2")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GroupOf(onEmpty)).Should().Be(((MediaGroupKind?)MediaGroupKind.Series, (int?)2));

        (await AdminSendAsync(HttpMethod.Delete, "/api/admin/series/3")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await GroupOf(onSetting)).Should().Be(((MediaGroupKind?)MediaGroupKind.Series, (int?)3));
    }

    private static UserStudyDeck GroupStudyDeck(MediaGroupKind kind, int groupId) =>
        new() { UserId = TestUsers.UserA, DeckType = StudyDeckType.MediaGroup, Name = "", IsActive = true, GroupKind = kind, GroupId = groupId };

    [Fact]
    public async Task FranchiseBuilder_SavesEdgesAndMembers()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B"), Deck(3, "C")], series: [Group(1, "Saga", SeriesKind.Series)]);

        var response = await AdminSendAsync(HttpMethod.Post, "/api/admin/franchise-builder/save", new
        {
            anchorDeckId = 1,
            addEdges = new[] { new { sourceDeckId = 1, targetDeckId = 2, relationshipType = 1 } },
            removeEdges = Array.Empty<object>(),
            addMembers = new[] { new { seriesId = 1, deckId = 2 }, new { seriesId = 1, deckId = 3 } },
            removeMembers = Array.Empty<object>()
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var franchise = await response.Content.ReadFromJsonAsync<FranchiseDto>();
        franchise!.FranchiseId.Should().NotBeNull();
        franchise.OriginalTitle.Should().Be("Saga");
        franchise.Nodes.Select(n => n.DeckId).Should().BeEquivalentTo([1, 2, 3]);
        franchise.Edges.Should().ContainSingle();
        franchise.Series.Single().MemberDeckIds.Should().BeEquivalentTo([2, 3]);
    }

    [Fact]
    public async Task FranchiseBuilder_SaveKeepsBoardDecksOutsideTheAnchorFranchise()
    {
        await SeedAsync([Deck(1, "Anchor"), Deck(2, "B"), Deck(3, "C")], series: [Group(1, "Saga", SeriesKind.Series)]);

        var response = await AdminSendAsync(HttpMethod.Post, "/api/admin/franchise-builder/save", new
        {
            anchorDeckId = 1,
            addEdges = new[] { new { sourceDeckId = 3, targetDeckId = 2, relationshipType = 1 } },
            removeEdges = Array.Empty<object>(),
            addMembers = new[] { new { seriesId = 1, deckId = 2 } },
            removeMembers = Array.Empty<object>(),
            boardDeckIds = new[] { 1, 2, 3 }
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var saved = (await response.Content.ReadFromJsonAsync<FranchiseDto>())!;
        saved.Nodes.Select(n => n.DeckId).Should().BeEquivalentTo([1, 2, 3]);
        saved.BoardOnlyDeckIds.Should().BeEmpty();
        saved.Edges.Should().ContainSingle();
        saved.Series.Single().MemberDeckIds.Should().BeEquivalentTo([2]);

        var reloaded = await GetAsync<FranchiseDto>("/api/admin/franchise-builder/1", admin: true);
        reloaded.Nodes.Select(n => n.DeckId).Should().BeEquivalentTo([1, 2, 3]);
        reloaded.Series.Single().MemberDeckIds.Should().BeEquivalentTo([2]);
    }

    [Fact]
    public async Task FranchiseBuilder_AddedDecksAreMarkedBoardOnly()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B"), Deck(3, "C")], [Rel(2, 1, DeckRelationshipType.Sequel)]);
        await FranchiseAsync(1);

        var dto = await GetAsync<FranchiseDto>("/api/admin/franchise-builder/1?add=3", admin: true);

        dto.Nodes.Select(n => n.DeckId).Should().BeEquivalentTo([1, 2, 3]);
        dto.BoardOnlyDeckIds.Should().Equal(3);
    }

    private Task<HttpResponseMessage> SaveBoardAsync(int anchorDeckId, params int[] board) =>
        AdminSendAsync(HttpMethod.Post, "/api/admin/franchise-builder/save", new
        {
            anchorDeckId,
            addEdges = Array.Empty<object>(),
            removeEdges = Array.Empty<object>(),
            addMembers = Array.Empty<object>(),
            removeMembers = Array.Empty<object>(),
            boardDeckIds = board
        });

    [Fact]
    public async Task FranchiseBuilder_RejectsBoardWithoutAnchor()
    {
        await SeedAsync([Deck(1, "Movie"), Deck(2, "Novel"), Deck(3, "Visual Novel")], [Rel(3, 2, DeckRelationshipType.Adaptation)], []);

        var response = await SaveBoardAsync(1, 2, 3);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task FranchiseBuilder_BoardBecomesOneFranchise_AcrossUnrelatedSeries()
    {
        await SeedAsync([Deck(1, "FF II"), Deck(2, "FF VII"), Deck(3, "FF VII Remake"), Deck(4, "FF XIII"), Deck(5, "FF XIII-2"), Deck(6, "Other")],
                        [Rel(3, 2, DeckRelationshipType.Sequel), Rel(5, 4, DeckRelationshipType.Sequel)],
                        [Group(1, "Final Fantasy VII", SeriesKind.Series, 2), Group(2, "Final Fantasy XIII", SeriesKind.Series, 4)]);
        var before = await FranchiseAsync(2);
        before.Nodes.Select(n => n.DeckId).Should().BeEquivalentTo([2, 3]);

        var response = await SaveBoardAsync(1, 1, 2, 3, 4, 5);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var saved = (await response.Content.ReadFromJsonAsync<FranchiseDto>())!;
        saved.FranchiseId.Should().NotBeNull();
        saved.BoardOnlyDeckIds.Should().BeEmpty();
        saved.Nodes.Select(n => n.DeckId).Should().BeEquivalentTo([1, 2, 3, 4, 5]);
        saved.Series.Select(s => s.SeriesId).Should().BeEquivalentTo([1, 2]);

        var fromSeries = await FranchiseAsync(4);
        fromSeries.FranchiseId.Should().Be(saved.FranchiseId);
        fromSeries.Nodes.Should().HaveCount(5);
        (await FranchiseAsync(6)).FranchiseId.Should().BeNull();
    }

    [Fact]
    public async Task FranchiseBuilder_DeckTakenOffTheBoard_LeavesTheFranchise()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B"), Deck(3, "C")]);
        (await SaveBoardAsync(1, 1, 2, 3)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await SaveBoardAsync(1, 1, 2);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await FranchiseAsync(1)).Nodes.Select(n => n.DeckId).Should().BeEquivalentTo([1, 2]);
        var dropped = await FranchiseAsync(3);
        dropped.FranchiseId.Should().BeNull();
    }

    [Fact]
    public async Task FranchiseBuilder_BoardsJoinedByALink_MergeIntoTheLargerOne()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B"), Deck(3, "C"), Deck(4, "D"), Deck(5, "E")]);
        await SaveBoardAsync(1, 1, 2, 3);
        await SaveBoardAsync(4, 4, 5);
        var big = (await FranchiseAsync(1)).FranchiseId;

        await SeedAsync([], [Rel(4, 3, DeckRelationshipType.Sequel)]);
        var merged = await FranchiseAsync(5);

        merged.FranchiseId.Should().Be(big);
        merged.Nodes.Should().HaveCount(5);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        (await db.FranchiseMembers.Select(m => m.FranchiseId).Distinct().ToListAsync()).Should().Equal(big!.Value);
        (await db.Franchises.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task FranchiseBuilder_RejectsCyclesAndLegacyTypes()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B"), Deck(3, "C")],
                        [Rel(1, 2, DeckRelationshipType.Sequel), Rel(2, 3, DeckRelationshipType.Sequel)]);

        var cycle = await AdminSendAsync(HttpMethod.Post, "/api/admin/franchise-builder/save", new
        {
            anchorDeckId = 1,
            addEdges = new[] { new { sourceDeckId = 1, targetDeckId = 3, relationshipType = 5 } },
            removeEdges = Array.Empty<object>(), addMembers = Array.Empty<object>(), removeMembers = Array.Empty<object>()
        });
        cycle.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await cycle.Content.ReadAsStringAsync()).Should().Contain("A (1)").And.Contain("C (3)");

        var legacy = await AdminSendAsync(HttpMethod.Post, "/api/admin/franchise-builder/save", new
        {
            anchorDeckId = 1,
            addEdges = new[] { new { sourceDeckId = 1, targetDeckId = 3, relationshipType = 7 } },
            removeEdges = Array.Empty<object>(), addMembers = Array.Empty<object>(), removeMembers = Array.Empty<object>()
        });
        legacy.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Story flow 3 -> 2 -> 1 plus 3 adapted into 1 runs the same way, so it is no loop.
        var sameWay = await AdminSendAsync(HttpMethod.Post, "/api/admin/franchise-builder/save", new
        {
            anchorDeckId = 1,
            addEdges = new[] { new { sourceDeckId = 3, targetDeckId = 1, relationshipType = 5 } },
            removeEdges = Array.Empty<object>(), addMembers = Array.Empty<object>(), removeMembers = Array.Empty<object>()
        });
        sameWay.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        (await db.DeckRelationships.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task SeriesDetail_AndVocabulary_MergeAcrossMembers()
    {
        await SeedAsync([Deck(1, "Anime", MediaType.Anime, chars: 1000, difficulty: 2f),
                         Deck(2, "Novel", MediaType.Novel, chars: 3000, difficulty: 4f),
                         Deck(3, "Excluded", MediaType.Anime)],
                        series: [Group(1, "Saga", SeriesKind.Series, 1, 2, 3)]);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            await db.WordForms.ExecuteDeleteAsync();
            await db.Definitions.ExecuteDeleteAsync();
            await db.JMDictWords.ExecuteDeleteAsync();
            for (var i = 1; i <= 3; i++)
            {
                db.JMDictWords.Add(new JmDictWord { WordId = i, PartsOfSpeech = ["noun"] });
                db.WordForms.Add(new JmDictWordForm { WordId = i, ReadingIndex = 0, Text = $"語{i}", RubyText = $"語{i}", FormType = JmDictFormType.KanjiForm });
                db.Definitions.Add(new JmDictDefinition { WordId = i, SenseIndex = 0, EnglishMeanings = [$"m{i}"], PartsOfSpeech = ["noun"] });
            }

            foreach (var (deckId, wordId, occurrences) in new[] { (1, 1, 5), (1, 2, 1), (2, 1, 7), (2, 3, 2), (3, 2, 100) })
                db.DeckWords.Add(new DeckWord { DeckId = deckId, WordId = wordId, ReadingIndex = 0, Occurrences = occurrences });
            await db.SaveChangesAsync();
        }

        int? franchiseId;
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<FranchiseSyncRunner>().RunAsync();
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            franchiseId = await db.Decks.AsNoTracking().Where(d => d.DeckId == 1).Select(d => d.FranchiseId).SingleAsync();
        }

        var detail = await GetAsync<SeriesDetailDto>("/api/admin/series/1", admin: true);
        detail.Members.Select(m => m.DeckId).Should().Equal(1, 2, 3);
        franchiseId.Should().NotBeNull();
        detail.FranchiseId.Should().Be(franchiseId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var stats = await MediaGroupStats.ComputeAsync(db, detail.Members.Select(m => m.DeckId).ToList());
            stats.UniqueWordCount.Should().Be(3);
            stats.CharacterCount.Should().Be(5000);
        }

        var vocab = await GetAsync<PaginatedResponse<DeckVocabularyListDto>>("/api/media-group/vocabulary?kind=2&id=1&sortBy=deckFreq&excludeDeckIds=3");
        vocab.TotalItems.Should().Be(3);
        vocab.Data.Deck.Should().BeNull();
        vocab.Data.Words.Select(w => (w.WordId, w.Occurrences)).Should().Equal((1, 12), (3, 2), (2, 1));

        var animeOnly = await GetAsync<PaginatedResponse<DeckVocabularyListDto>>("/api/media-group/vocabulary?kind=2&id=1&mediaTypes=1&sortBy=globalFreq");
        animeOnly.Data.Words.Select(w => w.WordId).Should().BeEquivalentTo([1, 2]);

        var search = await GetAsync<PaginatedResponse<List<SeriesSummaryDto>>>("/api/admin/series?query=sag&kind=1&limit=20", admin: true);
        search.Data.Should().ContainSingle().Which.Should().BeEquivalentTo(new { OriginalTitle = "Saga", DeckCount = 3 });
    }

    [Fact]
    public async Task MediaGroupStats_AreCharacterWeighted()
    {
        await SeedAsync([Deck(1, "Anime", MediaType.Anime, chars: 1000, difficulty: 2f),
                         Deck(2, "Novel", MediaType.Novel, chars: 3000, difficulty: 4f),
                         Deck(3, "Unscored", MediaType.Novel, chars: 9000, difficulty: 0f)]);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var stats = await MediaGroupStats.ComputeAsync(db, [1, 2, 3]);

        stats.Difficulty.Should().BeApproximately(3.5f, 0.001f);
        stats.CharacterCount.Should().Be(13000);
        (await MediaGroupStats.ComputeAsync(db, [3])).Difficulty.Should().Be(-1);
    }

    [Fact]
    public async Task Importer_TurnsGroupRelationsIntoMembership()
    {
        Deck Vn(int id, int year)
        {
            var deck = new Deck { DeckId = id, OriginalTitle = $"VN {id}", MediaType = MediaType.VisualNovel, ReleaseDate = new DateOnly(year, 1, 1) };
            deck.Links.Add(new Link { LinkType = LinkType.Vndb, Url = $"https://vndb.org/v{id}", Deck = deck });
            return deck;
        }
        await SeedAsync([Vn(1, 2005), Vn(2, 2001), Vn(3, 2010), Vn(4, 2012), Vn(5, 2015)]);

        MetadataRelation Ser(int target) => new()
        {
            ExternalId = $"v{target}", LinkType = LinkType.Vndb, RelationshipType = DeckRelationshipType.SameSeries,
            TargetMediaType = MediaType.VisualNovel
        };

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        await MetadataProviderHelper.ProcessRelations(db, 1, [Ser(2)]);
        var first = await db.Series.AsNoTracking().Include(s => s.Members).SingleAsync();
        first.OriginalTitle.Should().Be("VN 2");
        first.Kind.Should().Be(SeriesKind.Series);
        first.Members.Select(m => m.DeckId).Should().BeEquivalentTo([1, 2]);

        await MetadataProviderHelper.ProcessRelations(db, 3, [Ser(1)]);
        await MetadataProviderHelper.ProcessRelations(db, 2, [Ser(3)]);
        (await db.SeriesMembers.Where(m => m.SeriesId == first.SeriesId).Select(m => m.DeckId).ToListAsync()).Should().BeEquivalentTo([1, 2, 3]);

        await MetadataProviderHelper.ProcessRelations(db, 4, [Ser(5)]);
        await MetadataProviderHelper.ProcessRelations(db, 4, [Ser(1)]);
        (await db.SeriesMembers.CountAsync(m => m.DeckId == 4)).Should().Be(1);

        (await db.Series.CountAsync()).Should().Be(2);
        (await db.SeriesMembers.CountAsync(m => m.SeriesId == first.SeriesId)).Should().Be(3);
        (await db.DeckRelationships.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task DeckMetadataPatch_RejectsLegacyGroupTypes()
    {
        await SeedAsync([Deck(1, "A"), Deck(2, "B")]);

        var response = await AdminSendAsync(HttpMethod.Patch, "/api/admin/deck/1/metadata", new
        {
            relationships = new[] { new { sourceDeckId = 1, targetDeckId = 2, relationshipType = 8 } }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
