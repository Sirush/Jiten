using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

/// <summary>
/// Builds a load balancer seeded from a user's currently-scheduled review load. Counts are aggregated
/// per UTC day in SQL, so only (day, count) rows cross the wire instead of one row per scheduled card.
/// </summary>
public static class FsrsLoadBalancerSeeder
{
    public static async Task<DictionaryFsrsLoadBalancer> SeedAsync(UserDbContext userContext, string userId)
    {
        var now = DateTime.UtcNow;
        var loadByDay = await userContext.FsrsCards
                                         .AsNoTracking()
                                         .Where(c => c.UserId == userId
                                                     && c.Due > now
                                                     && c.Due < DateTime.MaxValue
                                                     && (c.State == FsrsState.Review
                                                         || c.State == FsrsState.Relearning
                                                         || c.State == FsrsState.Learning))
                                         .GroupBy(c => c.Due.Date)
                                         .Select(g => new { Day = g.Key, Count = g.Count() })
                                         .ToListAsync();

        return new DictionaryFsrsLoadBalancer(
            loadByDay.Select(x => KeyValuePair.Create(DateOnly.FromDateTime(x.Day), x.Count)));
    }
}
