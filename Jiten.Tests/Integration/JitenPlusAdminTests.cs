using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class JitenPlusAdminTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserPromoCredits.RemoveRange(userDb.UserPromoCredits);
        userDb.PromoCodes.RemoveRange(userDb.PromoCodes);
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeSubscriptionActive = false;
            user.IsLifetime = false;
            user.LifetimeSource = null;
            user.AdminPremiumOverride = false;
        }
        await userDb.SaveChangesAsync();

        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        jitenDb.Notifications.RemoveRange(jitenDb.Notifications);
        await jitenDb.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            jitenPlus.InvalidateTier(id);

        factory.Emails.Clear();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<JsonElement> GetStatus(string userId)
    {
        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/jiten-plus/status").WithUser(userId));
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ---- Grants ----

    [Fact]
    public async Task GrantDays_CreatesCredit_Notification_Email_AndFullTier()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jiten-plus/grant")
            .WithAdmin()
            .WithJsonContent(new { userIdOrName = TestUsers.UserA, kind = "days", days = 30, grantsFullTier = true, thankYouMessage = "Thanks for all your reports!" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var credit = await userDb.UserPromoCredits.FirstAsync(c => c.UserId == TestUsers.UserA);
        credit.PromoCodeId.Should().BeNull();
        credit.Source.Should().Be(PromoCreditSource.AdminGrant);
        credit.GrantsFullTier.Should().BeTrue();
        credit.RemainingDays.Should().Be(30);
        credit.ThankYouMessage.Should().Be("Thanks for all your reports!");

        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var notif = await jitenDb.Notifications.FirstAsync(n => n.UserId == TestUsers.UserA);
        notif.Message.Should().Contain("Thanks for all your reports!");

        factory.Emails.Sent.Should().Contain(e => e.Method == "SendJitenPlusGrantAsync");

        var status = await GetStatus(TestUsers.UserA);
        status.GetProperty("tier").GetString().Should().Be("full");
    }

    [Fact]
    public async Task GrantLifetime_SetsFlags_AndRejectsWhenAlreadyLifetime()
    {
        var grant = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jiten-plus/grant")
            .WithAdmin()
            .WithJsonContent(new { userIdOrName = TestUsers.UserB, kind = "lifetime", grantsFullTier = true, thankYouMessage = "Legend." });
        var response = await _client.SendAsync(grant);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var user = await userDb.Users.FirstAsync(u => u.Id == TestUsers.UserB);
            user.IsLifetime.Should().BeTrue();
            user.LifetimeSource.Should().Be(LifetimeSource.ContributorGrant);
        }

        var status = await GetStatus(TestUsers.UserB);
        status.GetProperty("tier").GetString().Should().Be("full");

        var again = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jiten-plus/grant")
            .WithAdmin()
            .WithJsonContent(new { userIdOrName = TestUsers.UserB, kind = "lifetime", grantsFullTier = true });
        var againResponse = await _client.SendAsync(again);
        againResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task RevokeLifetime_ContributorGrant_Succeeds_AndTierDrops()
    {
        var grant = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jiten-plus/grant")
            .WithAdmin()
            .WithJsonContent(new { userIdOrName = TestUsers.UserB, kind = "lifetime", grantsFullTier = true });
        (await _client.SendAsync(grant)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetStatus(TestUsers.UserB)).GetProperty("tier").GetString().Should().Be("full");

        var revoke = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jiten-plus/revoke-lifetime")
            .WithAdmin()
            .WithJsonContent(new { userIdOrName = TestUsers.UserB });
        (await _client.SendAsync(revoke)).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var user = await userDb.Users.FirstAsync(u => u.Id == TestUsers.UserB);
            user.IsLifetime.Should().BeFalse();
            user.LifetimeSource.Should().BeNull();
        }

        (await GetStatus(TestUsers.UserB)).GetProperty("tier").GetString().Should().Be("none");
    }

    [Fact]
    public async Task RevokeLifetime_PurchasedLifetime_Rejected()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var user = await userDb.Users.FirstAsync(u => u.Id == TestUsers.UserB);
            user.IsLifetime = true;
            user.LifetimeSource = LifetimeSource.WindowPurchase;
            await userDb.SaveChangesAsync();
        }

        var revoke = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jiten-plus/revoke-lifetime")
            .WithAdmin()
            .WithJsonContent(new { userIdOrName = TestUsers.UserB });
        (await _client.SendAsync(revoke)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var user = await userDb.Users.FirstAsync(u => u.Id == TestUsers.UserB);
            user.IsLifetime.Should().BeTrue();
            user.LifetimeSource.Should().Be(LifetimeSource.WindowPurchase);
        }
    }

    [Fact]
    public async Task RevokeLifetime_NonAdmin_Forbidden()
    {
        var revoke = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jiten-plus/revoke-lifetime")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { userIdOrName = TestUsers.UserB });
        (await _client.SendAsync(revoke)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GrantDays_Twice_SameUser_Allowed()
    {
        // Admin grants have a null PromoCodeId; the (UserId, PromoCodeId) unique index permits multiple NULLs,
        // so a user can receive more than one reward grant.
        for (var i = 0; i < 2; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jiten-plus/grant")
                .WithAdmin()
                .WithJsonContent(new { userIdOrName = TestUsers.UserA, kind = "days", days = 10, grantsFullTier = false });
            var response = await _client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var count = await userDb.UserPromoCredits.CountAsync(c => c.UserId == TestUsers.UserA);
        count.Should().Be(2);
    }

    [Fact]
    public async Task GrantDays_MissingDays_Fails()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jiten-plus/grant")
            .WithAdmin()
            .WithJsonContent(new { userIdOrName = TestUsers.UserA, kind = "days", grantsFullTier = true });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Grant_UnknownUser_Returns404()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jiten-plus/grant")
            .WithAdmin()
            .WithJsonContent(new { userIdOrName = "does-not-exist", kind = "days", days = 5, grantsFullTier = true });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- Promo code CRUD ----

    [Fact]
    public async Task PromoCode_CrudLifecycle()
    {
        // Create
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/admin/promo-codes")
            .WithAdmin()
            .WithJsonContent(new { durationDays = 7, grantsFullTier = false, description = "Launch giveaway" });
        var createResponse = await _client.SendAsync(create);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var codeId = created.GetProperty("codeId").GetInt32();
        created.GetProperty("code").GetString().Should().HaveLength(10);

        // List
        var list = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/promo-codes").WithAdmin());
        var listBody = await list.Content.ReadFromJsonAsync<JsonElement>();
        listBody.EnumerateArray().Should().Contain(c => c.GetProperty("codeId").GetInt32() == codeId);

        // Update (deactivate via PUT)
        var update = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/promo-codes/{codeId}")
            .WithAdmin()
            .WithJsonContent(new { isActive = false, maxUses = 50 });
        var updateResponse = await _client.SendAsync(update);
        updateResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await updateResponse.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("isActive").GetBoolean().Should().BeFalse();
        updated.GetProperty("maxUses").GetInt32().Should().Be(50);

        // Usage
        var usage = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/admin/promo-codes/{codeId}/usage").WithAdmin());
        usage.StatusCode.Should().Be(HttpStatusCode.OK);

        // Delete (soft)
        var delete = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/promo-codes/{codeId}").WithAdmin());
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var promo = await userDb.PromoCodes.FirstAsync(p => p.CodeId == codeId);
        promo.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task BulkGenerate_ProducesRequestedCount_AllUnique()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/promo-codes/bulk-generate")
            .WithAdmin()
            .WithJsonContent(new { count = 25, durationDays = 7, grantsFullTier = false, description = "Event" });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("count").GetInt32().Should().Be(25);

        var codes = body.GetProperty("codes").EnumerateArray().Select(c => c.GetString()).ToList();
        codes.Should().HaveCount(25);
        codes.Distinct().Should().HaveCount(25);
        codes.Should().OnlyContain(c => c!.Length == 10);
    }

    [Fact]
    public async Task GrantsLog_ListsAdminGrants()
    {
        var grant = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jiten-plus/grant")
            .WithAdmin()
            .WithJsonContent(new { userIdOrName = TestUsers.UserA, kind = "days", days = 15, grantsFullTier = true });
        await _client.SendAsync(grant);

        var log = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/admin/jiten-plus/grants").WithAdmin());
        log.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await log.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("dayGrants").GetArrayLength().Should().BeGreaterThanOrEqualTo(1);
    }

    // ---- Authorization ----

    [Fact]
    public async Task NonAdmin_CannotCreatePromoCode()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/promo-codes")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { durationDays = 7, grantsFullTier = false });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NonAdmin_CannotGrant()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/jiten-plus/grant")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { userIdOrName = TestUsers.UserB, kind = "days", days = 5, grantsFullTier = true });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
