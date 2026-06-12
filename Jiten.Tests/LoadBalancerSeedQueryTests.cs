using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Tests;

public class LoadBalancerSeedQueryTests
{
    /// <summary>
    /// The load-balancer seed query groups by <c>Due.Date</c> server-side. Integration tests run on
    /// SQLite, whose translation rules differ from Npgsql's (notably around timestamptz members), so a
    /// query that passes there can still throw "could not be translated" in production. ToQueryString
    /// compiles the SQL offline against the Npgsql provider without needing a database.
    /// </summary>
    [Fact]
    public void SeedQuery_GroupByDueDate_TranslatesOnNpgsql()
    {
        var options = new DbContextOptionsBuilder<UserDbContext>()
                      .UseNpgsql("Host=localhost;Database=translation_check_only")
                      .Options;
        using var context = new UserDbContext(options);

        var now = DateTime.UtcNow;
        var userId = Guid.Empty.ToString();
        var query = context.FsrsCards
                           .AsNoTracking()
                           .Where(c => c.UserId == userId
                                       && c.Due > now
                                       && c.Due < DateTime.MaxValue
                                       && (c.State == FsrsState.Review
                                           || c.State == FsrsState.Relearning
                                           || c.State == FsrsState.Learning))
                           .GroupBy(c => c.Due.Date)
                           .Select(g => new { Day = g.Key, Count = g.Count() });

        var sql = query.ToQueryString();

        Assert.Contains("GROUP BY", sql);
    }
}
