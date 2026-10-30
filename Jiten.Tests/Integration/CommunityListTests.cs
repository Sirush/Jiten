using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Controllers;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.CommunityLists;
using Jiten.Core.Services;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class CommunityListTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private const int TagCozy = 9101;
    private const int TagSchool = 9102;
    private static readonly int FreeMaxLists = new JitenPlusLimitsOptions().CommunityLists.Free;
    private static readonly int FreeMaxItems = new JitenPlusLimitsOptions().CommunityListItems.Free;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserPromoCredits.RemoveRange(userDb.UserPromoCredits);
        await userDb.UserCoverageChunks.ExecuteDeleteAsync();
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeSubscriptionActive = false;
            user.SubscriptionPeriodEnd = null;
            user.SubscriptionPlan = null;
            user.IsLifetime = false;
            user.LifetimeSource = null;
            user.AdminPremiumOverride = false;
            user.DisplayName = user.Id switch
            {
                TestUsers.UserA => "Alice",
                TestUsers.UserB => "Bobby",
                _ => null
            };
        }
        await userDb.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            jitenPlus.InvalidateTier(id);

        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        foreach (var (tagId, name) in new[] { (TagCozy, "Cozy"), (TagSchool, "School") })
        {
            if (!await jitenDb.Tags.AnyAsync(t => t.TagId == tagId))
                jitenDb.Tags.Add(new Tag { TagId = tagId, Name = name });
        }
        await jitenDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Helpers ------------------------------------------------------------

    private async Task<List<int>> SeedDecks(int count, int? parentId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var decks = Enumerable.Range(0, count)
                              .Select(i => new Deck { OriginalTitle = $"Deck {Guid.NewGuid():N}", MediaType = MediaType.Anime, ParentDeckId = parentId })
                              .ToList();
        db.Decks.AddRange(decks);
        await db.SaveChangesAsync();
        return decks.Select(d => d.DeckId).ToList();
    }

    private async Task SetMatureCoverage(string userId, Dictionary<int, float> coverage) =>
        await SetCoverage(userId, UserCoverageMetric.MatureCoverage, coverage);

    private async Task SetCoverage(string userId, UserCoverageMetric metric, Dictionary<int, float> coverage)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        foreach (var chunk in coverage.GroupBy(c => c.Key / 1024))
        {
            var values = new short[1024];
            foreach (var (deckId, percent) in chunk)
                values[deckId % 1024] = (short)(percent * 100);
            userDb.UserCoverageChunks.Add(new UserCoverageChunk
            {
                UserId = userId, Metric = (short)metric, ChunkIndex = chunk.Key, Values = values, ComputedAt = DateTime.UtcNow
            });
        }
        await userDb.SaveChangesAsync();
    }

    private async Task SetFull(string userId, bool full)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var user = await userDb.Users.FirstAsync(u => u.Id == userId);
        user.AdminPremiumOverride = full;
        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(userId);
    }

    private async Task SetTrial(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var code = new PromoCode { Code = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), DurationDays = 7, GrantsFullTier = false };
        userDb.PromoCodes.Add(code);
        await userDb.SaveChangesAsync();
        userDb.UserPromoCredits.Add(new UserPromoCredit
        {
            UserId = userId, PromoCodeId = code.CodeId, GrantsFullTier = false, RemainingDays = 7, GrantedAt = DateTime.UtcNow
        });
        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(userId);
    }

    private async Task Mutate(int listId, Action<CommunityList> mutate)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var list = await db.CommunityLists.FirstAsync(l => l.ListId == listId);
        mutate(list);
        await db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string url, string? userId = null, object? body = null, bool admin = false)
    {
        var request = new HttpRequestMessage(method, url);
        if (admin) request.WithAdmin();
        else if (userId != null) request.WithUser(userId);
        if (body != null) request.WithJsonContent(body);
        return _client.SendAsync(request);
    }

    private async Task<int> CreateList(string userId, string name = "My list", HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await Send(HttpMethod.Post, "/api/lists", userId, new { name });
        response.StatusCode.Should().Be(expected);
        if (expected != HttpStatusCode.OK) return 0;
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("listId").GetInt32();
    }

    private Task<HttpResponseMessage> PutDetails(int listId, string userId, CommunityListStatus status, string name = "My list",
                                                 int[]? genres = null, int[]? tagIds = null, bool admin = false) =>
        Send(HttpMethod.Put, $"/api/lists/{listId}", userId,
             new { name, description = "About", status = (int)status, genres = genres ?? [], tagIds = tagIds ?? [] }, admin);

    private Task<HttpResponseMessage> PutItems(int listId, string userId, IEnumerable<int> deckIds, Func<int, string?>? note = null) =>
        Send(HttpMethod.Put, $"/api/lists/{listId}/items", userId,
             new { items = deckIds.Select(id => new { deckId = id, note = note?.Invoke(id) }).ToList() });

    private async Task<int> CreatePublicList(string userId, string name, IEnumerable<int>? deckIds = null, int[]? genres = null, int[]? tagIds = null)
    {
        var id = await CreateList(userId, name);
        if (deckIds != null)
            (await PutItems(id, userId, deckIds)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PutDetails(id, userId, CommunityListStatus.Public, name, genres, tagIds)).StatusCode.Should().Be(HttpStatusCode.OK);
        return id;
    }

    private async Task<string> ErrorCode(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("code").GetString()!;
    }

    private async Task<List<int>> BrowseIds(string query, string? userId = null)
    {
        var response = await Send(HttpMethod.Get, $"/api/lists?{query}", userId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").EnumerateArray().Select(l => l.GetProperty("listId").GetInt32()).ToList();
    }

    private async Task<int> NotificationCount(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        return await db.Notifications.CountAsync(n => n.UserId == userId && n.Type == NotificationType.CommunityListMilestone);
    }

    // ---- Creation, identity and quotas --------------------------------------

    [Fact]
    public async Task Create_WithoutDisplayName_IsRejectedWithCode()
    {
        var response = await Send(HttpMethod.Post, "/api/lists", TestUsers.Admin, new { name = "Admin list" });
        (await ErrorCode(response)).Should().Be("display_name_required");
    }

    [Fact]
    public async Task Create_StartsAsDraftWithSlug()
    {
        var response = await Send(HttpMethod.Post, "/api/lists", TestUsers.UserA, new { name = "Slice-of-life you can read at N4" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("slug").GetString().Should().Be("slice-of-life-you-can-read-at-n4");

        var mine = await (await Send(HttpMethod.Get, "/api/lists/mine", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        mine.GetProperty("lists")[0].GetProperty("status").GetInt32().Should().Be((int)CommunityListStatus.Draft);
        mine.GetProperty("quota").GetProperty("maxLists").GetInt32().Should().Be(FreeMaxLists);
    }

    [Theory]
    [InlineData("Café au lait", "cafe-au-lait")]
    [InlineData("よつばと！", "")]
    [InlineData("  N4 -- reading!! ", "n4-reading")]
    public void Slug_FoldsAccentsAndDropsOtherScripts(string name, string expected) =>
        CommunityListController.MakeSlug(name).Should().Be(expected);

    [Fact]
    public async Task FreeUser_CapsAtTenLists()
    {
        for (var i = 0; i < FreeMaxLists; i++)
            await CreateList(TestUsers.UserA, $"List {i}");

        var response = await Send(HttpMethod.Post, "/api/lists", TestUsers.UserA, new { name = "One too many" });
        (await ErrorCode(response)).Should().Be("list_limit");
    }

    [Fact]
    public async Task TrialUser_GetsFreeCaps()
    {
        await SetTrial(TestUsers.UserA);
        for (var i = 0; i < FreeMaxLists; i++)
            await CreateList(TestUsers.UserA, $"List {i}");

        (await ErrorCode(await Send(HttpMethod.Post, "/api/lists", TestUsers.UserA, new { name = "Eleventh" }))).Should().Be("list_limit");
    }

    [Fact]
    public async Task FullUser_GetsHigherCaps()
    {
        await SetFull(TestUsers.UserA, true);
        for (var i = 0; i < FreeMaxLists + 1; i++)
            await CreateList(TestUsers.UserA, $"List {i}");

        var listId = await CreateList(TestUsers.UserA, "Big");
        var decks = await SeedDecks(FreeMaxItems + 1);
        (await PutItems(listId, TestUsers.UserA, decks)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Items_RejectOverCapChildrenDuplicatesAndLongNotes()
    {
        var listId = await CreateList(TestUsers.UserA);
        var decks = await SeedDecks(FreeMaxItems + 1);
        var children = await SeedDecks(1, decks[0]);

        (await ErrorCode(await PutItems(listId, TestUsers.UserA, decks))).Should().Be("item_limit");
        (await ErrorCode(await PutItems(listId, TestUsers.UserA, [decks[0], children[0]]))).Should().Be("invalid_item");
        (await ErrorCode(await PutItems(listId, TestUsers.UserA, [decks[0], decks[0]]))).Should().Be("duplicate_item");
        (await ErrorCode(await PutItems(listId, TestUsers.UserA, [decks[0]], _ => new string('a', 301)))).Should().Be("note_too_long");
    }

    [Fact]
    public async Task Text_IsSanitisedAndInvisibleNamesRejected()
    {
        (await ErrorCode(await Send(HttpMethod.Post, "/api/lists", TestUsers.UserA, new { name = "​ㅤ ‮" })))
            .Should().Be("invalid_name");

        var decks = await SeedDecks(1);
        var listId = await CreateList(TestUsers.UserA, "Good‮evil");
        await Send(HttpMethod.Put, $"/api/lists/{listId}", TestUsers.UserA,
                   new { name = "Good‮evil", description = "One\r\n\r\n\r\n\r\nTwo\u0007", status = (int)CommunityListStatus.Draft, genres = Array.Empty<int>(), tagIds = Array.Empty<int>() });
        await PutItems(listId, TestUsers.UserA, decks, _ => "​");

        var edit = await (await Send(HttpMethod.Get, $"/api/lists/{listId}/edit", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        edit.GetProperty("name").GetString().Should().Be("Goodevil");
        edit.GetProperty("description").GetString().Should().Be("One\n\nTwo");
        edit.GetProperty("items")[0].GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Text_RejectsOverLengthFields()
    {
        var longName = new string('a', CommunityList.MaxNameLength + 1);
        var longDescription = new string('a', CommunityList.MaxDescriptionLength + 1);

        (await ErrorCode(await Send(HttpMethod.Post, "/api/lists", TestUsers.UserA, new { name = longName })))
            .Should().Be("invalid_name");
        (await ErrorCode(await Send(HttpMethod.Post, "/api/lists", TestUsers.UserA, new { name = "Fine", description = longDescription })))
            .Should().Be("description_too_long");

        var listId = await CreateList(TestUsers.UserA);
        (await ErrorCode(await PutDetails(listId, TestUsers.UserA, CommunityListStatus.Draft, name: longName))).Should().Be("invalid_name");
        (await ErrorCode(await Send(HttpMethod.Put, $"/api/lists/{listId}", TestUsers.UserA,
                                    new { name = "Fine", description = longDescription, status = (int)CommunityListStatus.Draft, genres = Array.Empty<int>(), tagIds = Array.Empty<int>() })))
            .Should().Be("description_too_long");
        (await PutDetails(listId, TestUsers.UserA, CommunityListStatus.Draft, name: new string('a', CommunityList.MaxNameLength)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Report_RejectsEmptyAndOverLengthReasons()
    {
        var listId = await CreatePublicList(TestUsers.UserA, "Reported");
        (await ErrorCode(await Send(HttpMethod.Post, $"/api/lists/{listId}/report", TestUsers.UserB, new { reason = " " }))).Should().Be("invalid_reason");
        (await ErrorCode(await Send(HttpMethod.Post, $"/api/lists/{listId}/report", TestUsers.UserB,
                                    new { reason = new string('a', CommunityListReport.MaxReasonLength + 1) })))
            .Should().Be("invalid_reason");
        (await Send(HttpMethod.Post, $"/api/lists/{listId}/report", TestUsers.UserB, new { reason = new string('a', CommunityListReport.MaxReasonLength) }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Details_CapGenresAndTags()
    {
        var listId = await CreateList(TestUsers.UserA);
        (await ErrorCode(await PutDetails(listId, TestUsers.UserA, CommunityListStatus.Draft, genres: [1, 2, 3, 4]))).Should().Be("genre_limit");
        (await ErrorCode(await PutDetails(listId, TestUsers.UserA, CommunityListStatus.Draft, tagIds: [1, 2, 3, 4, 5, 6]))).Should().Be("tag_limit");
        (await ErrorCode(await PutDetails(listId, TestUsers.UserA, CommunityListStatus.Draft, genres: [99999]))).Should().Be("genre_limit");
        (await ErrorCode(await PutDetails(listId, TestUsers.UserA, CommunityListStatus.Draft, tagIds: [TagCozy, 999999]))).Should().Be("unknown_tag");
        (await PutDetails(listId, TestUsers.UserA, CommunityListStatus.Draft, genres: [1, 2, 3], tagIds: [TagCozy, TagSchool]))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OverCap_AfterDowngrade_KeepsEverythingEditableButBlocksGrowth()
    {
        await SetFull(TestUsers.UserA, true);
        var listIds = new List<int>();
        for (var i = 0; i < FreeMaxLists + 2; i++)
            listIds.Add(await CreateList(TestUsers.UserA, $"List {i}"));
        var decks = await SeedDecks(FreeMaxItems + 2);
        (await PutItems(listIds[0], TestUsers.UserA, decks.Take(FreeMaxItems))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PutDetails(listIds[0], TestUsers.UserA, CommunityListStatus.Public)).StatusCode.Should().Be(HttpStatusCode.OK);

        await SetFull(TestUsers.UserA, false);

        (await ErrorCode(await Send(HttpMethod.Post, "/api/lists", TestUsers.UserA, new { name = "New" }))).Should().Be("list_limit");
        (await PutDetails(listIds[1], TestUsers.UserA, CommunityListStatus.Draft, "Renamed")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Get, $"/api/lists/{listIds[0]}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await PutItems(listIds[0], TestUsers.UserA, decks.Take(FreeMaxItems - 1))).StatusCode.Should().Be(HttpStatusCode.OK);
        var swapped = decks.Take(FreeMaxItems - 2).Append(decks[FreeMaxItems + 1]).ToList();
        (await PutItems(listIds[0], TestUsers.UserA, swapped)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ErrorCode(await PutItems(listIds[0], TestUsers.UserA, decks.Take(FreeMaxItems + 1)))).Should().Be("item_limit");

        (await Send(HttpMethod.Delete, $"/api/lists/{listIds[2]}", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ---- Visibility ---------------------------------------------------------

    [Fact]
    public async Task Discussion_ExistsOnlyForPublicListsAndParentDecks()
    {
        var draft = await CreateList(TestUsers.UserA, "Draft");
        var published = await CreatePublicList(TestUsers.UserA, "Published");
        var parent = (await SeedDecks(1))[0];
        var child = (await SeedDecks(1, parent))[0];

        async Task<string?> TopicUrl(string path)
        {
            var response = await Send(HttpMethod.Get, path, TestUsers.UserA);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("topicUrl").GetString();
        }

        (await TopicUrl($"/api/forum/discussions/list/{published}")).Should().Be($"https://jiten.moe/lists/{published}");
        (await TopicUrl($"/api/forum/discussions/deck/{parent}")).Should().Be($"https://jiten.moe/decks/media/{parent}/detail");
        (await TopicUrl($"/api/forum/discussions/list/{draft}")).Should().BeNull();
        (await TopicUrl($"/api/forum/discussions/deck/{child}")).Should().BeNull();
        (await TopicUrl($"/api/forum/discussions/user/{published}")).Should().BeNull();
        (await TopicUrl($"/api/forum/discussions/list/{published}?previewTopic=43")).Should().Be($"https://jiten.moe/lists/{published}");
    }

    [Fact]
    public async Task StartDiscussion_NeedsASignedInUserAndAPublicList()
    {
        var draft = await CreateList(TestUsers.UserA, "Draft");
        var published = await CreatePublicList(TestUsers.UserA, "Published");

        (await Send(HttpMethod.Post, $"/api/forum/discussions/list/{published}/start")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, $"/api/forum/discussions/list/{draft}/start", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var started = await Send(HttpMethod.Post, $"/api/forum/discussions/list/{published}/start", TestUsers.UserB);
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("topicUrl").GetString().Should().Be($"https://jiten.moe/lists/{published}");
    }

    [Fact]
    public async Task StartDiscussion_OnlyForParentDecks()
    {
        var parent = (await SeedDecks(1))[0];
        var child = (await SeedDecks(1, parent))[0];

        (await Send(HttpMethod.Post, $"/api/forum/discussions/deck/{child}/start", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var started = await Send(HttpMethod.Post, $"/api/forum/discussions/deck/{parent}/start", TestUsers.UserB);
        started.StatusCode.Should().Be(HttpStatusCode.OK);
        (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("topicUrl").GetString().Should().Be($"https://jiten.moe/decks/media/{parent}/detail");
    }

    [Fact]
    public async Task Draft_IsOnlyVisibleToOwnerAndAdmin()
    {
        var listId = await CreateList(TestUsers.UserA);

        (await Send(HttpMethod.Get, $"/api/lists/{listId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Get, $"/api/lists/{listId}", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Get, $"/api/lists/{listId}/items", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Get, $"/api/lists/{listId}/edit", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Get, $"/api/lists/{listId}", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Get, $"/api/lists/{listId}", admin: true)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await BrowseIds("sort=newest")).Should().NotContain(listId);
    }

    [Fact]
    public async Task Unlisted_HidesListAndBlocksOwnerRepublish()
    {
        var listId = await CreatePublicList(TestUsers.UserA, "Takedown");
        (await Send(HttpMethod.Post, $"/api/lists/{listId}/report", TestUsers.UserB, new { reason = "Spam" })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Send(HttpMethod.Post, $"/api/admin/community-lists/{listId}/unlist", admin: true)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await BrowseIds("sort=top")).Should().NotContain(listId);
        (await Send(HttpMethod.Get, $"/api/lists/{listId}", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Get, $"/api/lists/{listId}", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ErrorCode(await PutDetails(listId, TestUsers.UserA, CommunityListStatus.Public, "Takedown"))).Should().Be("unlisted_by_moderation");
        (await PutDetails(listId, TestUsers.UserA, CommunityListStatus.Unlisted, "Takedown fixed")).StatusCode.Should().Be(HttpStatusCode.OK);

        var open = await (await Send(HttpMethod.Get, "/api/admin/list-reports", admin: true)).Content.ReadFromJsonAsync<JsonElement>();
        open.GetProperty("totalItems").GetInt32().Should().Be(0);
        var resolved = await (await Send(HttpMethod.Get, "/api/admin/list-reports?resolved=true", admin: true)).Content.ReadFromJsonAsync<JsonElement>();
        resolved.GetProperty("totalItems").GetInt32().Should().Be(1);

        (await Send(HttpMethod.Post, $"/api/admin/community-lists/{listId}/restore", admin: true)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await BrowseIds("sort=top")).Should().Contain(listId);
    }

    [Fact]
    public async Task Anonymous_CanReadButNotWrite()
    {
        var listId = await CreatePublicList(TestUsers.UserA, "Public");

        (await Send(HttpMethod.Get, "/api/lists")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Get, $"/api/lists/{listId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Send(HttpMethod.Post, "/api/lists", body: new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, $"/api/lists/{listId}/favourite")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, $"/api/lists/{listId}/report", body: new { reason = "x" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Post, $"/api/lists/{listId}/view")).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    // ---- Items, detail and paging -------------------------------------------

    [Fact]
    public async Task ReplaceItems_PersistsOrderAndTrimsNotes()
    {
        var decks = await SeedDecks(3);
        var listId = await CreateList(TestUsers.UserA);
        await Mutate(listId, l => l.UpdatedAt = DateTime.UtcNow.AddDays(-5));

        var order = new[] { decks[2], decks[0], decks[1] };
        (await PutItems(listId, TestUsers.UserA, order, id => id == decks[0] ? "  start here  " : "   ")).StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await (await Send(HttpMethod.Get, $"/api/lists/{listId}", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        detail.GetProperty("itemCount").GetInt32().Should().Be(3);
        detail.GetProperty("updatedAt").GetDateTime().Should().BeAfter(DateTime.UtcNow.AddMinutes(-1));
        var items = detail.GetProperty("items").GetProperty("data").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("deck").GetProperty("deckId").GetInt32()).Should().Equal(order);
        items.Select(i => i.GetProperty("position").GetInt32()).Should().Equal(1, 2, 3);
        items[1].GetProperty("note").GetString().Should().Be("start here");
        items[0].GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private Task<HttpResponseMessage> PostItems(int listId, string userId, IEnumerable<int> deckIds, int? position = null, string? note = null) =>
        Send(HttpMethod.Post, $"/api/lists/{listId}/items", userId,
             new { items = deckIds.Select(id => new { deckId = id, note }).ToList(), position });

    private async Task<List<int>> OrderedDeckIds(int listId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var rows = await db.CommunityListItems.Where(i => i.ListId == listId).OrderBy(i => i.Position).Select(i => new { i.DeckId, i.Position }).ToListAsync();
        rows.Select(r => r.Position).Should().Equal(Enumerable.Range(0, rows.Count));
        (await db.CommunityLists.Where(l => l.ListId == listId).Select(l => l.ItemCount).FirstAsync()).Should().Be(rows.Count);
        return rows.Select(r => r.DeckId).ToList();
    }

    [Fact]
    public async Task AddItems_AppendsSkipsExistingAndInsertsAtPosition()
    {
        var decks = await SeedDecks(5);
        var listId = await CreateList(TestUsers.UserA);
        (await PutItems(listId, TestUsers.UserA, decks.Take(2))).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await PostItems(listId, TestUsers.UserA, [decks[1], decks[2], decks[3]]);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("added").GetInt32().Should().Be(2);
        body.GetProperty("alreadyInList").GetInt32().Should().Be(1);
        body.GetProperty("itemCount").GetInt32().Should().Be(4);

        (await PostItems(listId, TestUsers.UserA, [decks[4]], position: 1, note: "  read second  ")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await OrderedDeckIds(listId)).Should().Equal(decks[0], decks[4], decks[1], decks[2], decks[3]);
    }

    [Fact]
    public async Task AddItems_RejectsWholeBatchOverCapAndInvalidTitles()
    {
        var decks = await SeedDecks(FreeMaxItems + 1);
        var child = (await SeedDecks(1, decks[0]))[0];
        var listId = await CreateList(TestUsers.UserA);
        (await PutItems(listId, TestUsers.UserA, decks.Take(FreeMaxItems - 1))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ErrorCode(await PostItems(listId, TestUsers.UserA, decks.Skip(FreeMaxItems - 1)))).Should().Be("item_limit");
        (await ErrorCode(await PostItems(listId, TestUsers.UserA, [child]))).Should().Be("invalid_item");
        (await ErrorCode(await PostItems(listId, TestUsers.UserA, []))).Should().Be("no_items");
        (await OrderedDeckIds(listId)).Should().HaveCount(FreeMaxItems - 1);

        (await PostItems(listId, TestUsers.UserA, [decks[0], decks[FreeMaxItems - 1]])).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RemoveItem_CompactsAndReturnsWhatPutsItBack()
    {
        var decks = await SeedDecks(3);
        var listId = await CreateList(TestUsers.UserA);
        (await PutItems(listId, TestUsers.UserA, decks, id => id == decks[1] ? "middle" : null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await Send(HttpMethod.Delete, $"/api/lists/{listId}/items/{decks[1]}", TestUsers.UserA);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var removed = await response.Content.ReadFromJsonAsync<JsonElement>();
        removed.GetProperty("position").GetInt32().Should().Be(1);
        removed.GetProperty("note").GetString().Should().Be("middle");
        (await OrderedDeckIds(listId)).Should().Equal(decks[0], decks[2]);

        (await Send(HttpMethod.Delete, $"/api/lists/{listId}/items/{decks[1]}", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await PostItems(listId, TestUsers.UserA, [decks[1]], position: 1, note: "middle")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await OrderedDeckIds(listId)).Should().Equal(decks);
    }

    [Fact]
    public async Task ItemEdits_AreOwnerOnly()
    {
        var decks = await SeedDecks(2);
        var listId = await CreatePublicList(TestUsers.UserA, "Mine", decks.Take(1));

        (await PostItems(listId, TestUsers.UserB, [decks[1]])).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Send(HttpMethod.Delete, $"/api/lists/{listId}/items/{decks[0]}", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PostItems(listId, TestUsers.UserA, [decks[1]], note: null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Membership_CountsSelectedTitlesInEachOwnList()
    {
        var decks = await SeedDecks(3);
        var full = await CreateList(TestUsers.UserA, "Full");
        (await PutItems(full, TestUsers.UserA, decks)).StatusCode.Should().Be(HttpStatusCode.OK);
        var partial = await CreateList(TestUsers.UserA, "Partial");
        (await PutItems(partial, TestUsers.UserA, decks.Take(1))).StatusCode.Should().Be(HttpStatusCode.OK);
        await CreatePublicList(TestUsers.UserB, "Someone else's", decks);

        var response = await Send(HttpMethod.Post, "/api/lists/mine/membership", TestUsers.UserA, new { deckIds = decks.Take(2).ToList() });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var lists = body.GetProperty("lists").EnumerateArray().ToDictionary(l => l.GetProperty("listId").GetInt32(), l => l.GetProperty("contained").GetInt32());
        lists.Should().BeEquivalentTo(new Dictionary<int, int> { [full] = 2, [partial] = 1 });
        body.GetProperty("quota").GetProperty("maxItems").GetInt32().Should().Be(FreeMaxItems);

        (await Send(HttpMethod.Post, "/api/lists/mine/membership", null, new { deckIds = decks })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ItemPages_CoverEveryItemOnceInOrder()
    {
        var decks = await SeedDecks(7);
        var listId = await CreatePublicList(TestUsers.UserA, "Paged", decks);

        var seen = new List<int>();
        for (var offset = 0; offset < 7; offset += 3)
        {
            var page = await (await Send(HttpMethod.Get, $"/api/lists/{listId}/items?offset={offset}&limit=3")).Content.ReadFromJsonAsync<JsonElement>();
            page.GetProperty("totalItems").GetInt32().Should().Be(7);
            seen.AddRange(page.GetProperty("data").EnumerateArray().Select(i => i.GetProperty("deck").GetProperty("deckId").GetInt32()));
        }

        seen.Should().Equal(decks);
    }

    [Fact]
    public async Task ItemSearch_MatchesAnyTitleIgnoringSpacesAndKeepsCuratorRanks()
    {
        var decks = await SeedDecks(4);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var seeded = await db.Decks.Where(d => decks.Contains(d.DeckId)).ToDictionaryAsync(d => d.DeckId);
            seeded[decks[1]].RomajiTitle = "Kimi no Na wa";
            seeded[decks[2]].EnglishTitle = "Your Name Again";
            db.DeckTitles.Add(new DeckTitle { DeckId = decks[3], Title = "Kiminonawa Alias", TitleType = DeckTitleType.Alias });
            await db.SaveChangesAsync();
        }

        var listId = await CreatePublicList(TestUsers.UserA, "Searchable", decks);

        var page = await (await Send(HttpMethod.Get, $"/api/lists/{listId}/items?q=KIMINO%20NAWA")).Content.ReadFromJsonAsync<JsonElement>();
        page.GetProperty("totalItems").GetInt32().Should().Be(2);
        var items = page.GetProperty("data").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("deck").GetProperty("deckId").GetInt32()).Should().Equal(decks[1], decks[3]);
        items.Select(i => i.GetProperty("position").GetInt32()).Should().Equal(2, 4);

        var detail = await (await Send(HttpMethod.Get, $"/api/lists/{listId}?q=your%20name")).Content.ReadFromJsonAsync<JsonElement>();
        detail.GetProperty("itemCount").GetInt32().Should().Be(4);
        detail.GetProperty("items").GetProperty("data").EnumerateArray().Select(i => i.GetProperty("deck").GetProperty("deckId").GetInt32())
              .Should().Equal(decks[2]);

        var none = await (await Send(HttpMethod.Get, $"/api/lists/{listId}/items?q=nothing")).Content.ReadFromJsonAsync<JsonElement>();
        none.GetProperty("totalItems").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Detail_CarriesViewerStatusesOnlyWhenLoggedIn()
    {
        var decks = await SeedDecks(4);
        var listId = await CreatePublicList(TestUsers.UserA, "Statuses", decks);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserDeckPreferences.AddRange(
                new UserDeckPreference { UserId = TestUsers.UserB, DeckId = decks[0], Status = DeckStatus.Completed },
                new UserDeckPreference { UserId = TestUsers.UserB, DeckId = decks[1], Status = DeckStatus.Planning },
                new UserDeckPreference { UserId = TestUsers.UserB, DeckId = decks[2], Status = DeckStatus.Planning });
            await userDb.SaveChangesAsync();
        }

        var detail = await (await Send(HttpMethod.Get, $"/api/lists/{listId}", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        var counts = detail.GetProperty("statusCounts").EnumerateArray()
                           .ToDictionary(c => c.GetProperty("status").GetInt32(), c => c.GetProperty("count").GetInt32());
        counts.Should().BeEquivalentTo(new Dictionary<int, int> { [(int)DeckStatus.Planning] = 2, [(int)DeckStatus.Completed] = 1 });
        detail.GetProperty("coverage").GetProperty("values").GetArrayLength().Should().Be(4);
        detail.GetProperty("coverage").GetProperty("easiest").ValueKind.Should().Be(JsonValueKind.Null);
        detail.GetProperty("items").GetProperty("data")[0].GetProperty("deck").GetProperty("status").GetInt32().Should().Be((int)DeckStatus.Completed);

        var anonymous = await (await Send(HttpMethod.Get, $"/api/lists/{listId}")).Content.ReadFromJsonAsync<JsonElement>();
        anonymous.GetProperty("statusCounts").ValueKind.Should().Be(JsonValueKind.Null);
        anonymous.GetProperty("coverage").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task CoverageSort_PutsEasiestFirstAndKeepsCuratorRanks()
    {
        var decks = await SeedDecks(3);
        var listId = await CreatePublicList(TestUsers.UserA, "Coverage", decks);
        await SetMatureCoverage(TestUsers.UserB, new() { [decks[0]] = 70, [decks[1]] = 95.5f, [decks[2]] = 85 });

        var detail = await (await Send(HttpMethod.Get, $"/api/lists/{listId}?sort=coverage", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        var coverage = detail.GetProperty("coverage");
        coverage.GetProperty("values").EnumerateArray().Select(v => v.GetSingle()).Should().Equal(95.5f, 85f, 70f);
        coverage.GetProperty("easiest").GetProperty("deckId").GetInt32().Should().Be(decks[1]);
        coverage.GetProperty("easiest").GetProperty("coverage").GetSingle().Should().Be(95.5f);

        var items = detail.GetProperty("items").GetProperty("data").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("deck").GetProperty("deckId").GetInt32()).Should().Equal(decks[1], decks[2], decks[0]);
        items.Select(i => i.GetProperty("position").GetInt32()).Should().Equal(2, 3, 1);

        var page = await (await Send(HttpMethod.Get, $"/api/lists/{listId}/items?sort=coverage&offset=1&limit=1", TestUsers.UserB))
                         .Content.ReadFromJsonAsync<JsonElement>();
        page.GetProperty("data")[0].GetProperty("deck").GetProperty("deckId").GetInt32().Should().Be(decks[2]);
        page.GetProperty("totalItems").GetInt32().Should().Be(3);

        var anonymous = await (await Send(HttpMethod.Get, $"/api/lists/{listId}/items?sort=coverage")).Content.ReadFromJsonAsync<JsonElement>();
        anonymous.GetProperty("data").EnumerateArray().Select(i => i.GetProperty("deck").GetProperty("deckId").GetInt32()).Should().Equal(decks);
    }

    [Fact]
    public async Task SentenceCoverage_OnlyReachesJitenPlusViewers()
    {
        var decks = await SeedDecks(1);
        var listId = await CreatePublicList(TestUsers.UserA, "Sentences", decks);
        await SetMatureCoverage(TestUsers.UserB, new() { [decks[0]] = 90 });
        await SetCoverage(TestUsers.UserB, UserCoverageMetric.ReadableSentences, new() { [decks[0]] = 60 });

        async Task<JsonElement> FirstDeck() =>
            (await (await Send(HttpMethod.Get, $"/api/lists/{listId}", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").GetProperty("data")[0].GetProperty("deck");

        (await FirstDeck()).GetProperty("readableSentences").ValueKind.Should().Be(JsonValueKind.Null);

        await SetFull(TestUsers.UserB, true);
        (await FirstDeck()).GetProperty("readableSentences").GetSingle().Should().Be(60);
    }

    [Fact]
    public async Task Views_AreNeverInPublicPayloads()
    {
        var listId = await CreatePublicList(TestUsers.UserA, "Viewed", await SeedDecks(1));
        var raw = await (await Send(HttpMethod.Get, $"/api/lists/{listId}")).Content.ReadAsStringAsync();
        raw.Should().NotContainEquivalentOf("views");
        var browse = await (await Send(HttpMethod.Get, "/api/lists")).Content.ReadAsStringAsync();
        browse.Should().NotContainEquivalentOf("views");
    }

    [Fact]
    public async Task Views_FlushIntoDailyRows_SkippingOwner()
    {
        var listId = await CreatePublicList(TestUsers.UserA, "Counted");
        (await Send(HttpMethod.Post, $"/api/lists/{listId}/view", TestUsers.UserA)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Post, $"/api/lists/{listId}/view", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Post, $"/api/lists/{listId}/view", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        await factory.Services.GetRequiredService<ICommunityListActivityBuffer>().FlushAsync();

        var admin = await (await Send(HttpMethod.Get, "/api/admin/community-lists", admin: true)).Content.ReadFromJsonAsync<JsonElement>();
        var row = admin.GetProperty("data").EnumerateArray().Single(l => l.GetProperty("listId").GetInt32() == listId);
        row.GetProperty("viewsTotal").GetInt32().Should().Be(1);
    }

    // ---- Favourites and milestones ------------------------------------------

    [Fact]
    public async Task Favourite_IsIdempotentAndRejectsOwnAndDrafts()
    {
        var listId = await CreatePublicList(TestUsers.UserA, "Fav");
        var draftId = await CreateList(TestUsers.UserA, "Draft");

        for (var i = 0; i < 2; i++)
        {
            var response = await Send(HttpMethod.Post, $"/api/lists/{listId}/favourite", TestUsers.UserB);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("favouriteCount").GetInt32().Should().Be(1);
        }

        var favourited = await (await Send(HttpMethod.Get, "/api/lists/favourited", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        favourited.GetProperty("data").EnumerateArray().Select(l => l.GetProperty("listId").GetInt32()).Should().Equal(listId);

        for (var i = 0; i < 2; i++)
        {
            var body = await (await Send(HttpMethod.Delete, $"/api/lists/{listId}/favourite", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("favouriteCount").GetInt32().Should().Be(0);
        }

        (await ErrorCode(await Send(HttpMethod.Post, $"/api/lists/{listId}/favourite", TestUsers.UserA))).Should().Be("own_list");
        (await Send(HttpMethod.Post, $"/api/lists/{draftId}/favourite", TestUsers.UserB)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Favourited_PagesMostRecentFirst()
    {
        var listIds = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var listId = await CreatePublicList(TestUsers.UserA, $"Fav {i}");
            await Send(HttpMethod.Post, $"/api/lists/{listId}/favourite", TestUsers.UserB);
            listIds.Add(listId);
        }

        var first = await (await Send(HttpMethod.Get, "/api/lists/favourited?limit=2", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await Send(HttpMethod.Get, "/api/lists/favourited?limit=2&offset=2", TestUsers.UserB)).Content.ReadFromJsonAsync<JsonElement>();

        first.GetProperty("totalItems").GetInt32().Should().Be(3);
        first.GetProperty("data").EnumerateArray().Concat(second.GetProperty("data").EnumerateArray())
             .Select(l => l.GetProperty("listId").GetInt32())
             .Should().Equal(listIds.AsEnumerable().Reverse());
    }

    [Fact]
    public async Task Milestone_NotifiesOnceEvenWhenFavouriteFlips()
    {
        var listId = await CreatePublicList(TestUsers.UserA, "Milestone");
        await Mutate(listId, l => l.FavouriteCount = 9);

        await Send(HttpMethod.Post, $"/api/lists/{listId}/favourite", TestUsers.UserB);
        (await NotificationCount(TestUsers.UserA)).Should().Be(1);

        await Send(HttpMethod.Delete, $"/api/lists/{listId}/favourite", TestUsers.UserB);
        await Send(HttpMethod.Post, $"/api/lists/{listId}/favourite", TestUsers.UserB);
        (await NotificationCount(TestUsers.UserA)).Should().Be(1);
    }

    [Fact]
    public async Task Milestone_JumpAnnouncesOnlyTheHighest()
    {
        var listId = await CreatePublicList(TestUsers.UserA, "Jump");
        await Mutate(listId, l => l.FavouriteCount = 49);

        await Send(HttpMethod.Post, $"/api/lists/{listId}/favourite", TestUsers.UserB);

        (await NotificationCount(TestUsers.UserA)).Should().Be(1);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        (await db.CommunityLists.Where(l => l.ListId == listId).Select(l => l.FavouriteMilestoneNotified).FirstAsync()).Should().Be(50);
        (await db.Notifications.FirstAsync(n => n.UserId == TestUsers.UserA)).Title.Should().Contain("50");
    }

    [Fact]
    public async Task Recount_RepairsDrift()
    {
        var listId = await CreatePublicList(TestUsers.UserA, "Drift", await SeedDecks(2));
        await Send(HttpMethod.Post, $"/api/lists/{listId}/favourite", TestUsers.UserB);
        await Mutate(listId, l =>
        {
            l.FavouriteCount = 40;
            l.ItemCount = 9;
        });

        (await Send(HttpMethod.Post, "/api/admin/community-lists/recount", admin: true)).StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await (await Send(HttpMethod.Get, $"/api/lists/{listId}")).Content.ReadFromJsonAsync<JsonElement>();
        detail.GetProperty("favouriteCount").GetInt32().Should().Be(1);
        var mine = await (await Send(HttpMethod.Get, "/api/lists/mine", TestUsers.UserA)).Content.ReadFromJsonAsync<JsonElement>();
        mine.GetProperty("lists")[0].GetProperty("itemCount").GetInt32().Should().Be(2);
    }

    // ---- Reports ------------------------------------------------------------

    [Fact]
    public async Task Report_PushesToDiscordOnceAndSecondOpenReportConflicts()
    {
        factory.DiscordReports.Clear();
        var listId = await CreatePublicList(TestUsers.UserA, "Reported");
        (await Send(HttpMethod.Post, $"/api/lists/{listId}/report", TestUsers.UserB, new { reason = "Spam" })).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Send(HttpMethod.Post, $"/api/lists/{listId}/report", TestUsers.UserB, new { reason = "Spam again" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCode(await Send(HttpMethod.Post, $"/api/lists/{listId}/report", TestUsers.UserA, new { reason = "Mine" }))).Should().Be("own_list");

        var sent = factory.DiscordReports.Sent.Should().ContainSingle().Subject;
        sent.ReporterId.Should().Be(TestUsers.UserB);
        sent.Description.Should().Contain($"https://jiten.moe/lists/{listId}/").And.Contain("Spam");
    }

    // ---- Browse -------------------------------------------------------------

    [Fact]
    public async Task Browse_SortsByTopNewestAndPopular()
    {
        var older = await CreatePublicList(TestUsers.UserA, "Older");
        var newer = await CreatePublicList(TestUsers.UserA, "Newer");
        await Mutate(older, l =>
        {
            l.PublishedAt = DateTime.UtcNow.AddDays(-3);
            l.FavouriteCount = 50;
        });

        (await BrowseIds("sort=top")).Should().Equal(older, newer);
        (await BrowseIds("sort=newest")).Should().Equal(newer, older);

        await Send(HttpMethod.Post, $"/api/lists/{newer}/favourite", TestUsers.UserB);
        (await BrowseIds("sort=popular&days=7")).Should().Equal(newer, older);
    }

    [Fact]
    public async Task Browse_RandomIsStablePerSeed()
    {
        var ids = new List<int>();
        for (var i = 0; i < 9; i++)
            ids.Add(await CreatePublicList(TestUsers.UserA, $"Random {i}"));

        var first = (await BrowseIds("sort=random&seed=4242&limit=5")).Concat(await BrowseIds("sort=random&seed=4242&limit=5&offset=5")).ToList();
        first.Should().BeEquivalentTo(ids);
        first.Should().OnlyHaveUniqueItems();

        var again = (await BrowseIds("sort=random&seed=4242&limit=9")).ToList();
        again.Should().Equal(first);

        var other = await BrowseIds("sort=random&seed=777&limit=9");
        other.Should().NotEqual(first);

        var unseeded = await (await Send(HttpMethod.Get, "/api/lists?sort=random")).Content.ReadFromJsonAsync<JsonElement>();
        unseeded.GetProperty("seed").GetInt32().Should().BePositive();
    }

    [Fact]
    public async Task Browse_FiltersByNameGenreAndTag()
    {
        var cozy = await CreatePublicList(TestUsers.UserA, "Cozy reads", genres: [(int)Genre.SliceOfLife], tagIds: [TagCozy]);
        var school = await CreatePublicList(TestUsers.UserA, "School stories", genres: [(int)Genre.SliceOfLife, (int)Genre.Comedy], tagIds: [TagSchool]);
        await CreatePublicList(TestUsers.UserA, "Action pack", genres: [(int)Genre.Action]);

        (await BrowseIds("q=cozy")).Should().Equal(cozy);
        (await BrowseIds($"genres={(int)Genre.SliceOfLife}&sort=newest")).Should().BeEquivalentTo([cozy, school]);
        (await BrowseIds($"genres={(int)Genre.SliceOfLife},{(int)Genre.Comedy}")).Should().Equal(school);
        (await BrowseIds($"tags={TagCozy}")).Should().Equal(cozy);
        (await BrowseIds($"genres={(int)Genre.SliceOfLife}&excludeTags={TagSchool}")).Should().Equal(cozy);
        var facets = await (await Send(HttpMethod.Get, $"/api/lists/facets?genres={(int)Genre.SliceOfLife}")).Content.ReadFromJsonAsync<JsonElement>();
        facets.GetProperty("genres").GetProperty(((int)Genre.SliceOfLife).ToString()).GetInt32().Should().Be(2);
        facets.GetProperty("genres").GetProperty(((int)Genre.Comedy).ToString()).GetInt32().Should().Be(1);
        facets.GetProperty("tags").GetProperty(TagCozy.ToString()).GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Browse_CapsFilterIdsAndIgnoresOverlongQueries()
    {
        await CreatePublicList(TestUsers.UserA, new string('a', CommunityList.MaxNameLength));

        var tooMany = string.Join(',', Enumerable.Range(1, 51));
        (await ErrorCode(await Send(HttpMethod.Get, $"/api/lists?excludeTags={tooMany}"))).Should().Be("filter_limit");
        (await ErrorCode(await Send(HttpMethod.Get, $"/api/lists/facets?tags={tooMany}"))).Should().Be("filter_limit");
        (await BrowseIds($"excludeTags={string.Join(',', Enumerable.Range(1000, 50))}")).Should().HaveCount(1);

        (await BrowseIds($"q={new string('a', CommunityList.MaxNameLength)}")).Should().HaveCount(1);
        (await BrowseIds($"q={new string('a', CommunityList.MaxNameLength + 1)}")).Should().BeEmpty();
    }

    [Fact]
    public async Task Summary_ShowsOwnerDisplayNameAndCovers()
    {
        var decks = await SeedDecks(5);
        var listId = await CreatePublicList(TestUsers.UserA, "Covers", decks);

        var response = await Send(HttpMethod.Get, "/api/lists?sort=top");
        var list = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data")[0];
        list.GetProperty("listId").GetInt32().Should().Be(listId);
        list.GetProperty("ownerDisplayName").GetString().Should().Be("Alice");
        list.GetProperty("previewCoverNames").GetArrayLength().Should().Be(5);
        list.GetProperty("status").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
