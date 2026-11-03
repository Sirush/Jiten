using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Authentication;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public abstract class MetadataEditTestBase(JitenWebApplicationFactory factory) : IAsyncLifetime
{
    protected readonly JitenWebApplicationFactory Factory = factory;
    protected readonly HttpClient Client = factory.CreateClient();

    protected const int DeckId = 1;
    protected const int OtherDeckId = 2;
    protected const int ThirdDeckId = 3;
    protected const int SubdeckId = 4;
    protected const int MissingDeckId = 999;
    protected const int TagAlpha = 11;
    protected const int TagBeta = 22;
    protected const int OtherFranchiseId = 7;
    protected const string Source = "https://vndb.org/v1474";

    protected const string DeckText = "前島鹿之助は部室に入った。\n「鹿之助くん！」ときらりが呼んだ。\nきらりは先輩と話した。\n前島鹿之助、前島鹿之助。";

    protected static class Users
    {
        public const string NoName = "10000000-0000-0000-0000-000000000001";
        public const string Unconfirmed = "10000000-0000-0000-0000-000000000002";
        public const string Author = "10000000-0000-0000-0000-000000000003";
        public const string Checker = "10000000-0000-0000-0000-000000000004";
        public const string YoungChecker = "10000000-0000-0000-0000-000000000005";
        public const string PlusFan = "10000000-0000-0000-0000-000000000006";
        public const string Reviewer = "10000000-0000-0000-0000-000000000007";
        public const string Editor = "10000000-0000-0000-0000-000000000008";
        public const string Vandal = "10000000-0000-0000-0000-000000000009";
        public const string Admin = TestUsers.Admin;
    }

    private sealed record SeedUser(string Id, string? DisplayName, bool EmailConfirmed, int AgeDays, string? Role = null, bool JitenPlusFull = false);

    private static readonly SeedUser[] SeedUsers =
    [
        new(Users.NoName, null, true, 60),
        new(Users.Unconfirmed, "unconfirmed", false, 60),
        new(Users.Author, "tsukimi", true, 60),
        new(Users.Checker, "nekomata", true, 60),
        new(Users.YoungChecker, "hoshizora", true, 3),
        new(Users.PlusFan, "plusfan", true, 60, JitenPlusFull: true),
        new(Users.Reviewer, "kaede", true, 60, nameof(UserRole.Reviewer)),
        new(Users.Editor, "henshuu", true, 60, nameof(UserRole.Editor)),
        new(Users.Vandal, "vandal", true, 60),
        new(Users.Admin, "TestAdmin", true, 400, nameof(UserRole.Administrator))
    ];

    public virtual async Task InitializeAsync()
    {
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            await db.MetadataEditChecks.ExecuteDeleteAsync();
            await db.MetadataEdits.ExecuteDeleteAsync();
            await db.DeckMetadataExclusions.ExecuteDeleteAsync();
            await db.MetadataContributors.ExecuteDeleteAsync();
            await db.MetadataEditSettings.ExecuteDeleteAsync();
            await db.DeckRelationships.ExecuteDeleteAsync();
            await db.Decks.Where(d => d.ParentDeckId != null).ExecuteDeleteAsync();
        }

        await Factory.ResetDatabaseAsync();

        using (var scope = Factory.Services.CreateScope())
        {
            await SeedDecksAsync(scope.ServiceProvider.GetRequiredService<JitenDbContext>());
            await SeedUsersAsync(scope.ServiceProvider.GetRequiredService<UserDbContext>());
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task SeedDecksAsync(JitenDbContext db)
    {
        await db.Tags.ExecuteDeleteAsync();
        db.Tags.AddRange(new Tag { TagId = TagAlpha, Name = "Alpha" }, new Tag { TagId = TagBeta, Name = "Beta" });
        db.Set<Franchise>().Add(new Franchise { FranchiseId = OtherFranchiseId, OriginalTitle = "Other Franchise" });
        await db.SaveChangesAsync();

        db.Decks.AddRange(
            new Deck
            {
                DeckId = DeckId, OriginalTitle = "きらきら", RomajiTitle = "Kira Kira", EnglishTitle = "Kira Kira",
                MediaType = MediaType.VisualNovel, ReleaseDate = new DateOnly(2007, 3, 30),
                CreationDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), RawText = new DeckRawText(DeckText)
            },
            new Deck { DeckId = OtherDeckId, OriginalTitle = "ほかの話", MediaType = MediaType.Novel, FranchiseId = OtherFranchiseId },
            new Deck { DeckId = ThirdDeckId, OriginalTitle = "三番目", MediaType = MediaType.Novel });
        await db.SaveChangesAsync();

        db.Decks.Add(new Deck { DeckId = SubdeckId, ParentDeckId = DeckId, OriginalTitle = "第一章", MediaType = MediaType.VisualNovel });
        db.Add(new DeckGenre { DeckId = DeckId, Genre = Genre.Action });
        db.Add(new DeckTag { DeckId = DeckId, TagId = TagAlpha, Percentage = 40 });
        db.Add(new DeckTag { DeckId = DeckId, TagId = TagBeta, Percentage = 60 });
        db.Add(new Link { DeckId = DeckId, LinkType = LinkType.Vndb, Url = "https://vndb.org/v1474", Deck = null! });
        await db.SaveChangesAsync();
    }

    /// <summary>UserDbContext stamps CreatedAt on insert, so account age is backdated with ExecuteUpdate; trust reads roles from AspNetUserRoles, not the test role header.</summary>
    private static async Task SeedUsersAsync(UserDbContext userDb)
    {
        foreach (var role in new[] { nameof(UserRole.Administrator), nameof(UserRole.Editor), nameof(UserRole.Reviewer) })
        {
            if (!await userDb.Roles.AnyAsync(r => r.Id == role))
                userDb.Roles.Add(new IdentityRole { Id = role, Name = role, NormalizedName = role.ToUpperInvariant() });
        }

        foreach (var seed in SeedUsers)
        {
            if (!await userDb.Users.AnyAsync(u => u.Id == seed.Id))
                userDb.Users.Add(new User
                {
                    Id = seed.Id, UserName = seed.Id, NormalizedUserName = seed.Id.ToUpperInvariant(), Email = $"{seed.Id}@test.local",
                    NormalizedEmail = $"{seed.Id}@TEST.LOCAL", SecurityStamp = Guid.NewGuid().ToString()
                });
        }

        await userDb.SaveChangesAsync();

        var ids = SeedUsers.Select(s => s.Id).ToList();
        await userDb.UserRoles.Where(r => ids.Contains(r.UserId)).ExecuteDeleteAsync();

        foreach (var seed in SeedUsers)
        {
            var createdAt = DateTime.UtcNow.AddDays(-seed.AgeDays);
            await userDb.Users.Where(u => u.Id == seed.Id)
                        .ExecuteUpdateAsync(u => u.SetProperty(x => x.DisplayName, seed.DisplayName)
                                                  .SetProperty(x => x.NormalizedDisplayName, seed.DisplayName == null ? null : seed.DisplayName.ToUpperInvariant())
                                                  .SetProperty(x => x.EmailConfirmed, seed.EmailConfirmed)
                                                  .SetProperty(x => x.AdminPremiumOverride, seed.JitenPlusFull)
                                                  .SetProperty(x => x.CreatedAt, createdAt));
            if (seed.Role != null)
                userDb.UserRoles.Add(new IdentityUserRole<string> { UserId = seed.Id, RoleId = seed.Role });
        }

        await userDb.SaveChangesAsync();
    }

    protected async Task<(HttpStatusCode Status, JsonNode? Body)> SendAsync(HttpMethod method, string url, string? userId = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (userId == Users.Admin)
            request.WithAdmin();
        else if (userId != null)
            request.WithUser(userId);
        if (body != null)
            request.WithJsonContent(body);

        var response = await Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
    }

    protected Task<(HttpStatusCode Status, JsonNode? Body)> SubmitAsync(string userId, object body, int deckId = DeckId) =>
        SendAsync(HttpMethod.Post, $"/api/media-deck/{deckId}/metadata-edits", userId, body);

    protected async Task<JsonNode> OkAsync(Task<(HttpStatusCode Status, JsonNode? Body)> call)
    {
        var (status, body) = await call;
        status.Should().Be(HttpStatusCode.OK, body?.ToJsonString());
        return body!;
    }

    protected Task<JsonNode> SubmitOkAsync(string userId, object body, int deckId = DeckId) => OkAsync(SubmitAsync(userId, body, deckId));

    protected async Task<string> ErrorAsync(Task<(HttpStatusCode Status, JsonNode? Body)> call, HttpStatusCode expected)
    {
        var (status, body) = await call;
        status.Should().Be(expected, body?.ToJsonString());
        return body?["message"]?.GetValue<string>() ?? "";
    }

    protected static List<int> Ints(JsonNode? node) => node?.AsArray().Select(n => n!.GetValue<int>()).ToList() ?? [];

    protected static long EditId(JsonNode submitResult, int index = 0) => submitResult["editIds"]![index]!.GetValue<long>();

    protected async Task<T> DbAsync<T>(Func<JitenDbContext, Task<T>> read)
    {
        using var scope = Factory.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<JitenDbContext>());
    }

    protected async Task DbAsync(Func<JitenDbContext, Task> write)
    {
        using var scope = Factory.Services.CreateScope();
        await write(scope.ServiceProvider.GetRequiredService<JitenDbContext>());
    }

    protected Task<Deck> DeckAsync(int deckId = DeckId) =>
        DbAsync(db => db.Decks.AsNoTracking()
                        .Include(d => d.Titles).Include(d => d.DeckGenres).Include(d => d.DeckTags).Include(d => d.DictionaryEntries)
                        .AsSplitQuery()
                        .FirstAsync(d => d.DeckId == deckId));

    protected Task<int> NotificationCountAsync(string userId, NotificationType type) =>
        DbAsync(db => db.Notifications.CountAsync(n => n.UserId == userId && n.Type == type));
}
