using FluentAssertions;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// Exercises <see cref="DecrementPromoCreditsJob"/> against the SQLite user DB with a fixed "today" so the
/// pause / ordering / one-per-day / ends-tomorrow rules are all deterministic.
/// </summary>
public class DecrementPromoCreditsJobTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 7, 14);

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
            user.SubscriptionPeriodEnd = null;
            user.LifetimeSource = null;
        }
        await userDb.SaveChangesAsync();
        factory.Emails.Clear();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private DecrementPromoCreditsJob Job() => new(
        factory.Services.GetRequiredService<IDbContextFactory<UserDbContext>>(),
        factory.Services.GetRequiredService<IJitenPlusService>(),
        factory.Emails,
        NullLogger<DecrementPromoCreditsJob>.Instance);

    private async Task AddCredit(string userId, int remainingDays, bool grantsFull, DateTime grantedAt, DateOnly? lastDecrement = null)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserPromoCredits.Add(new UserPromoCredit
        {
            UserId = userId,
            PromoCodeId = null,
            Source = PromoCreditSource.AdminGrant,
            GrantsFullTier = grantsFull,
            RemainingDays = remainingDays,
            GrantedAt = grantedAt,
            LastDecrementDate = lastDecrement
        });
        await userDb.SaveChangesAsync();
    }

    private async Task SetUserFlags(string userId, bool subActive = false, bool lifetime = false, DateTime? periodEnd = null)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var user = await userDb.Users.FirstAsync(u => u.Id == userId);
        user.StripeSubscriptionActive = subActive;
        user.IsLifetime = lifetime;
        user.SubscriptionPeriodEnd = periodEnd;
        await userDb.SaveChangesAsync();
    }

    private async Task<List<UserPromoCredit>> GetCredits(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserPromoCredits.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderBy(c => c.UserPromoCreditId)
            .ToListAsync();
    }

    [Fact]
    public async Task PausedByActiveSubscription()
    {
        await AddCredit(TestUsers.UserA, 5, grantsFull: false, DateTime.UtcNow);
        await SetUserFlags(TestUsers.UserA, subActive: true);

        await Job().RunForDate(Today);

        var credits = await GetCredits(TestUsers.UserA);
        credits[0].RemainingDays.Should().Be(5);
        credits[0].LastDecrementDate.Should().BeNull();
    }

    [Fact]
    public async Task PausedByLifetime()
    {
        await AddCredit(TestUsers.UserA, 5, grantsFull: false, DateTime.UtcNow);
        await SetUserFlags(TestUsers.UserA, lifetime: true);

        await Job().RunForDate(Today);

        (await GetCredits(TestUsers.UserA))[0].RemainingDays.Should().Be(5);
    }

    [Fact]
    public async Task NotPausedDuringGraceOnlyState()
    {
        // Grace = raw flag false but period end still in the future. The job uses raw flags only, so it counts down.
        await AddCredit(TestUsers.UserA, 5, grantsFull: false, DateTime.UtcNow);
        await SetUserFlags(TestUsers.UserA, subActive: false, periodEnd: DateTime.UtcNow.AddDays(1));

        await Job().RunForDate(Today);

        (await GetCredits(TestUsers.UserA))[0].RemainingDays.Should().Be(4);
    }

    [Fact]
    public async Task DecrementsOnlyOncePerDay()
    {
        await AddCredit(TestUsers.UserA, 5, grantsFull: false, DateTime.UtcNow);

        await Job().RunForDate(Today);
        await Job().RunForDate(Today);

        var credit = (await GetCredits(TestUsers.UserA))[0];
        credit.RemainingDays.Should().Be(4);
        credit.LastDecrementDate.Should().Be(Today);
    }

    [Fact]
    public async Task ConsumesFullBeforeTrial_EvenWhenFullIsNewer()
    {
        await AddCredit(TestUsers.UserA, 3, grantsFull: false, DateTime.UtcNow.AddDays(-10)); // trial, older
        await AddCredit(TestUsers.UserA, 2, grantsFull: true, DateTime.UtcNow);               // full, newer

        await Job().RunForDate(Today);

        var credits = await GetCredits(TestUsers.UserA);
        var trial = credits.Single(c => !c.GrantsFullTier);
        var full = credits.Single(c => c.GrantsFullTier);
        full.RemainingDays.Should().Be(1);
        trial.RemainingDays.Should().Be(3);
    }

    [Fact]
    public async Task FifoWithinTierClass()
    {
        await AddCredit(TestUsers.UserA, 3, grantsFull: false, DateTime.UtcNow.AddDays(-5)); // older trial
        await AddCredit(TestUsers.UserA, 3, grantsFull: false, DateTime.UtcNow);             // newer trial

        await Job().RunForDate(Today);

        var credits = await GetCredits(TestUsers.UserA);
        credits[0].RemainingDays.Should().Be(2); // older consumed first
        credits[1].RemainingDays.Should().Be(3);
    }

    [Fact]
    public async Task SetsFullyUsedAtWhenReachingZero()
    {
        await AddCredit(TestUsers.UserA, 1, grantsFull: false, DateTime.UtcNow);

        await Job().RunForDate(Today);

        var credit = (await GetCredits(TestUsers.UserA))[0];
        credit.RemainingDays.Should().Be(0);
        credit.FullyUsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task SendsEndsTomorrowEmail_WhenOneDayRemainsAfterDecrement()
    {
        await AddCredit(TestUsers.UserA, 2, grantsFull: false, DateTime.UtcNow);

        await Job().RunForDate(Today);

        factory.Emails.Sent.Should().Contain(e => e.Method == "SendPromoAccessEndsTomorrowAsync");
    }

    [Fact]
    public async Task NoEndsTomorrowEmail_WhenMoreThanOneDayRemains()
    {
        await AddCredit(TestUsers.UserA, 3, grantsFull: false, DateTime.UtcNow);

        await Job().RunForDate(Today);

        factory.Emails.Sent.Should().NotContain(e => e.Method == "SendPromoAccessEndsTomorrowAsync");
    }
}
