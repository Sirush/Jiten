using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class RequestBoostTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserPromoCredits.RemoveRange(userDb.UserPromoCredits);
        userDb.PromoCodes.RemoveRange(userDb.PromoCodes);
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeSubscriptionActive = false;
            user.SubscriptionPeriodEnd = null;
            user.SubscriptionPlan = null;
            user.IsLifetime = false;
            user.LifetimeSource = null;
            user.AdminPremiumOverride = false;
        }
        await userDb.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            jitenPlus.InvalidateTier(id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Grants Trial tier (grantsFull:false) — proves the plain [JitenPlus] gate accepts trial users.
    private async Task GrantTrial(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var code = new PromoCode { Code = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), DurationDays = 7, GrantsFullTier = false };
        userDb.PromoCodes.Add(code);
        await userDb.SaveChangesAsync();
        userDb.UserPromoCredits.Add(new UserPromoCredit
        {
            UserId = userId,
            PromoCodeId = code.CodeId,
            GrantsFullTier = false,
            RemainingDays = 7,
            GrantedAt = DateTime.UtcNow
        });
        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(userId);
    }

    private async Task<int> CreateRequest(string userId, string title)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/requests")
            .WithUser(userId)
            .WithJsonContent(new { title, mediaType = (int)MediaType.Anime });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetInt32();
    }

    private Task<HttpResponseMessage> Boost(string userId, int id) =>
        _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/requests/{id}/boost").WithUser(userId));

    private async Task RejectRequest(int id)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/requests/{id}/status")
            .WithAdmin()
            .WithJsonContent(new { status = (int)MediaRequestStatus.Rejected, adminNote = "no" });
        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<JsonElement> GetRequestDto(string userId, int id)
    {
        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/requests/{id}").WithUser(userId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<List<JsonElement>> GetList(string userId, string sort = "votes")
    {
        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/requests?sort={sort}&limit=50").WithUser(userId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("data").EnumerateArray().ToList();
    }

    [Fact]
    public async Task Boost_TrialUser_Succeeds_IncrementsCount_AndReturnsBalance()
    {
        await GrantTrial(TestUsers.UserA);
        var id = await CreateRequest(TestUsers.UserB, "Boost me");

        var response = await Boost(TestUsers.UserA, id);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("boostCount").GetInt32().Should().Be(1);
        var balance = body.GetProperty("balance");
        balance.GetProperty("limit").GetInt32().Should().Be(5);
        balance.GetProperty("used").GetInt32().Should().Be(1);
        balance.GetProperty("remaining").GetInt32().Should().Be(4);

        var dto = await GetRequestDto(TestUsers.UserA, id);
        dto.GetProperty("boostCount").GetInt32().Should().Be(1);
        dto.GetProperty("hasUserBoosted").GetBoolean().Should().BeTrue();
        // Votes and boosts stay separate — boosting must not touch the upvote count.
        dto.GetProperty("upvoteCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Boost_OwnRequest_IsAllowed()
    {
        await GrantTrial(TestUsers.UserA);
        var id = await CreateRequest(TestUsers.UserA, "My own");

        var response = await Boost(TestUsers.UserA, id);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Boost_ChangesTopSortOrder()
    {
        await GrantTrial(TestUsers.UserA);
        var older = await CreateRequest(TestUsers.UserB, "Older request");
        var newer = await CreateRequest(TestUsers.UserB, "Newer request");

        // Both have 1 auto-upvote; the newer one wins the CreatedAt tie-break in the top sort.
        var before = await GetList(TestUsers.UserA);
        before[0].GetProperty("id").GetInt32().Should().Be(newer);

        // Boosting the older one adds +5, lifting its effective score above the newer one.
        (await Boost(TestUsers.UserA, older)).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await GetList(TestUsers.UserA);
        after[0].GetProperty("id").GetInt32().Should().Be(older);
    }

    [Fact]
    public async Task Boost_SameRequest_SecondTime_Returns409()
    {
        await GrantTrial(TestUsers.UserA);
        var id = await CreateRequest(TestUsers.UserB, "Boost twice");

        (await Boost(TestUsers.UserA, id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await Boost(TestUsers.UserA, id);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Count stays at 1 — the rejected boost added nothing.
        var dto = await GetRequestDto(TestUsers.UserA, id);
        dto.GetProperty("boostCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Boost_SameRequest_InLaterMonth_StillReturns409()
    {
        await GrantTrial(TestUsers.UserA);
        var id = await CreateRequest(TestUsers.UserB, "Boost once ever");

        (await Boost(TestUsers.UserA, id)).StatusCode.Should().Be(HttpStatusCode.OK);

        // Backdate the boost into a previous month. Boosting is once-per-request-ever, so even with a
        // fresh monthly allowance the same request must not be boostable again.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
            var boost = await db.MediaRequestBoosts.FirstAsync(b => b.MediaRequestId == id && b.UserId == TestUsers.UserA);
            boost.CreatedAt = DateTime.UtcNow.AddMonths(-2);
            await db.SaveChangesAsync();
        }

        var second = await Boost(TestUsers.UserA, id);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var dto = await GetRequestDto(TestUsers.UserA, id);
        dto.GetProperty("boostCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Boost_SixthInMonth_Returns422_WithResetInfo()
    {
        await GrantTrial(TestUsers.UserA);
        var ids = new List<int>();
        for (var i = 0; i < 6; i++)
            ids.Add(await CreateRequest(TestUsers.UserB, $"Request {i}"));

        for (var i = 0; i < 5; i++)
            (await Boost(TestUsers.UserA, ids[i])).StatusCode.Should().Be(HttpStatusCode.OK);

        var sixth = await Boost(TestUsers.UserA, ids[5]);
        sixth.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var body = await sixth.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("limit").GetInt32().Should().Be(5);
        body.GetProperty("remaining").GetInt32().Should().Be(0);
        body.TryGetProperty("resetAt", out var resetAt).Should().BeTrue();
        resetAt.GetDateTime().Should().BeAfter(DateTime.UtcNow);

        // The sixth target was never boosted.
        var dto = await GetRequestDto(TestUsers.UserA, ids[5]);
        dto.GetProperty("boostCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Boost_NonPlusUser_Returns403_WithJitenPlusPayload()
    {
        var id = await CreateRequest(TestUsers.UserB, "Gated");

        // UserA has no Jiten+ tier.
        var response = await Boost(TestUsers.UserA, id);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("jitenPlus").GetBoolean().Should().BeTrue();
        body.GetProperty("feature").GetString().Should().Be("request-boosts");
    }

    [Fact]
    public async Task Boost_NonBoostableStatus_Returns400()
    {
        await GrantTrial(TestUsers.UserA);
        var id = await CreateRequest(TestUsers.UserB, "To reject");
        await RejectRequest(id);

        var response = await Boost(TestUsers.UserA, id);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Boost_VisibleInListDto_ForBooster()
    {
        await GrantTrial(TestUsers.UserA);
        var id = await CreateRequest(TestUsers.UserB, "In list");
        (await Boost(TestUsers.UserA, id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await GetList(TestUsers.UserA);
        var dto = list.Single(r => r.GetProperty("id").GetInt32() == id);
        dto.GetProperty("boostCount").GetInt32().Should().Be(1);
        dto.GetProperty("hasUserBoosted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task BoostBalance_ReflectsUsage()
    {
        await GrantTrial(TestUsers.UserA);

        var initial = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/requests/boost-balance").WithUser(TestUsers.UserA));
        initial.StatusCode.Should().Be(HttpStatusCode.OK);
        var initialBody = await initial.Content.ReadFromJsonAsync<JsonElement>();
        initialBody.GetProperty("remaining").GetInt32().Should().Be(5);

        var id = await CreateRequest(TestUsers.UserB, "Balance target");
        (await Boost(TestUsers.UserA, id)).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/requests/boost-balance").WithUser(TestUsers.UserA));
        var afterBody = await after.Content.ReadFromJsonAsync<JsonElement>();
        afterBody.GetProperty("remaining").GetInt32().Should().Be(4);
        afterBody.GetProperty("used").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task BoostBalance_NonPlusUser_Returns403()
    {
        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/requests/boost-balance").WithUser(TestUsers.UserA));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminUserSummary_CountsBoostsCast_SeparatelyFromUpvotes()
    {
        await GrantTrial(TestUsers.UserA);
        var id1 = await CreateRequest(TestUsers.UserB, "Summary 1");
        var id2 = await CreateRequest(TestUsers.UserB, "Summary 2");
        (await Boost(TestUsers.UserA, id1)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Boost(TestUsers.UserA, id2)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/api/admin/request-user-summary/{TestUsers.UserA}").WithAdmin());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("boostCount").GetInt32().Should().Be(2);
        // UserA boosted but never upvoted — the two signals stay independent.
        body.GetProperty("upvoteCount").GetInt32().Should().Be(0);
    }
}
