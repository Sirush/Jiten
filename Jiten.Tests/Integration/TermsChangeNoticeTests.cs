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

public class TermsChangeNoticeTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        factory.Emails.Clear();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.BillingEmailLogs.RemoveRange(userDb.BillingEmailLogs);
        userDb.UserPromoCredits.RemoveRange(userDb.UserPromoCredits);
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.Email = $"{user.Id}@example.com";
            user.StripeSubscriptionActive = false;
            user.StripeSubscriptionId = null;
            user.StripeCancelAtPeriodEnd = false;
            user.SubscriptionPeriodEnd = null;
            user.SubscriptionPlan = null;
            user.IsLifetime = false;
            user.LifetimeSource = null;
            user.AdminPremiumOverride = false;
        }
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task Mutate(string userId, Action<Jiten.Core.Data.Authentication.User> mutate)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var user = await userDb.Users.FirstAsync(u => u.Id == userId);
        mutate(user);
        await userDb.SaveChangesAsync();
    }

    private async Task<JsonElement> Run(bool dryRun)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/legal/terms-change-notices?dryRun={dryRun}")
            .WithAdmin();
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // Seeds one of each Jiten+ population; only the real Stripe subscriber may be selected.
    private async Task SeedPopulations()
    {
        // UserA: genuine paid recurring subscriber.
        await Mutate(TestUsers.UserA, u =>
        {
            u.StripeSubscriptionActive = true;
            u.StripeSubscriptionId = "sub_paid";
            u.SubscriptionPlan = SubscriptionPlan.Yearly;
            u.SubscriptionPeriodEnd = DateTime.UtcNow.AddDays(200);
        });

        // UserB: free Jiten+ via admin day-grant + admin override — never bought anything.
        await Mutate(TestUsers.UserB, u => u.AdminPremiumOverride = true);
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserPromoCredits.Add(new UserPromoCredit
            {
                UserId = TestUsers.UserB,
                PromoCodeId = null,
                Source = PromoCreditSource.AdminGrant,
                GrantsFullTier = true,
                RemainingDays = 365,
                GrantedAt = DateTime.UtcNow
            });
            await userDb.SaveChangesAsync();
        }

        // Admin: lifetime holder — nothing renews, no notice owed.
        await Mutate(TestUsers.Admin, u =>
        {
            u.IsLifetime = true;
            u.LifetimeSource = LifetimeSource.ContributorGrant;
        });
    }

    [Fact]
    public async Task DryRun_SelectsOnlyRealPaidSubscribers_NeverGrantHolders()
    {
        await SeedPopulations();

        var result = await Run(dryRun: true);

        var subscribers = result.GetProperty("subscribers").EnumerateArray().ToList();
        subscribers.Should().ContainSingle();
        var row = subscribers[0];
        row.GetProperty("email").GetString().Should().Be($"{TestUsers.UserA}@example.com");
        row.GetProperty("status").GetString().Should().Be("would-send");
        row.GetProperty("emailSubject").GetString().Should().NotBeNullOrEmpty();
        row.GetProperty("emailHtml").GetString().Should().Contain("Terms of Sale");

        // Dry run sends nothing.
        factory.Emails.Sent.Should().NotContain(e => e.Method == nameof(IEmailService.SendTermsChangeNoticeAsync));
    }

    [Fact]
    public async Task Send_EmailsSubscriberOnce_ThenSkipsOnRerun()
    {
        await SeedPopulations();

        var first = await Run(dryRun: false);
        first.GetProperty("sent").GetInt32().Should().Be(1);

        var sends = factory.Emails.Sent.Where(e => e.Method == nameof(IEmailService.SendTermsChangeNoticeAsync)).ToList();
        sends.Should().ContainSingle().Which.Recipient.Should().Be($"{TestUsers.UserA}@example.com");

        var second = await Run(dryRun: false);
        second.GetProperty("sent").GetInt32().Should().Be(0);
        second.GetProperty("skipped").GetInt32().Should().Be(1);
        second.GetProperty("subscribers").EnumerateArray().Single().GetProperty("status").GetString().Should().Be("already-sent");

        factory.Emails.Sent.Count(e => e.Method == nameof(IEmailService.SendTermsChangeNoticeAsync)).Should().Be(1);
    }
}
