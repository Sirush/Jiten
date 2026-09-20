using Jiten.Core;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

public class StubDescriptionSearchService(IDbContextFactory<JitenDbContext> contextFactory, ILogger<DescriptionSearchService> logger)
    : DescriptionSearchService(contextFactory, () => null, logger)
{
    /// <summary>Deck ids returned in order, with descending scores.</summary>
    public List<int> RankedDeckIds { get; } = new();

    public override bool IsAvailable => true;

    public override List<Match> Search(string query, int limit, IReadOnlySet<int>? allowedDeckIds = null, bool cutNoise = true)
    {
        return RankedDeckIds
               .Where(id => allowedDeckIds == null || allowedDeckIds.Contains(id))
               .Take(limit)
               .Select((id, i) => new Match(id, 0.9f - i * 0.1f, 0.9f - i * 0.1f, 0f, []))
               .ToList();
    }
}
