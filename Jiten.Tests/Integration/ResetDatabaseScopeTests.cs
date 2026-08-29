using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ResetDatabaseScopeTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => factory.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ResetClearsStudyDecksAndTheirWords()
    {
        using (var seedScope = factory.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<UserDbContext>();
            var deck = new UserStudyDeck
                       {
                           UserId = TestUsers.UserA,
                           DeckType = StudyDeckType.StaticWordList,
                           Name = "Leftover deck",
                           Words = { new UserStudyDeckWord { WordId = 1, ReadingIndex = 0 } }
                       };
            seedDb.UserStudyDecks.Add(deck);
            await seedDb.SaveChangesAsync();
        }

        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.UserStudyDecks.CountAsync()).Should().Be(0);
        (await userDb.UserStudyDeckWords.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ResetKeepsSeededTestUsers()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            (await userDb.Users.AnyAsync(u => u.Id == id)).Should().BeTrue();
    }
}
