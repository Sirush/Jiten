using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Services;

public static class ParseDataEpochStore
{
    public static async Task<int> ReadAsync(JitenDbContext db, CancellationToken ct = default)
    {
        return await db.ParseDataEpochs.AsNoTracking()
                       .Where(e => e.Id == ParseDataEpoch.SingletonId)
                       .Select(e => e.Epoch)
                       .FirstOrDefaultAsync(ct);
    }

    /// <summary>Call only after the dictionary change has committed; bumping first lets a reader save old ids under the new version.</summary>
    public static async Task BumpAsync(IDbContextFactory<JitenDbContext> contextFactory, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var updated = await db.ParseDataEpochs
                              .Where(e => e.Id == ParseDataEpoch.SingletonId)
                              .ExecuteUpdateAsync(s => s.SetProperty(e => e.Epoch, e => e.Epoch + 1)
                                                        .SetProperty(e => e.UpdatedAt, now), ct);
        if (updated > 0)
            return;

        db.ParseDataEpochs.Add(new ParseDataEpoch { Id = ParseDataEpoch.SingletonId, Epoch = 1, UpdatedAt = now });
        await db.SaveChangesAsync(ct);
    }
}
