using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class LoadBalancerSeederTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Seeder_GroupsScheduledLoadOnTheUsersLocalDay()
    {
        // Two cards at 20:00 UTC on the same date: local day D+1 for a UTC+9 user, day D in UTC.
        var due = DateTime.UtcNow.Date.AddDays(10).AddHours(20);
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, 1, 0, state: FsrsState.Review, stability: 20, due: due));
            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, 2, 0, state: FsrsState.Review, stability: 20, due: due));
            await userDb.SaveChangesAsync();
        }

        using var readScope = factory.Services.CreateScope();
        var ctx = readScope.ServiceProvider.GetRequiredService<UserDbContext>();
        var balancer = await FsrsLoadBalancerSeeder.SeedAsync(ctx, TestUsers.UserA, offsetHours: 9);

        balancer.GetLoad(due.AddHours(7)).Should().Be(2, "03:00 UTC the next day is the same local day");
        balancer.GetLoad(due.AddHours(-10)).Should().Be(0, "10:00 UTC the same day is the previous local day");
    }
}
