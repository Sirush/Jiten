using System.Runtime.CompilerServices;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Jiten.Api.Services;

/// <summary>Bumps the profile cache generation of users whose profile rows a save touched; ExecuteUpdate and ExecuteDelete bypass it.</summary>
public sealed class ProfileGenerationInterceptor(IProfileCacheService profileCache) : SaveChangesInterceptor
{
    private static readonly ConditionalWeakTable<DbContext, HashSet<string>> Pending = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result,
                                                                          CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (Take(eventData.Context) is { } userIds)
            _ = profileCache.BumpGenerationAsync(userIds);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
                                                           CancellationToken cancellationToken = default)
    {
        if (Take(eventData.Context) is { } userIds)
            await profileCache.BumpGenerationAsync(userIds);
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => Take(eventData.Context);

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Take(eventData.Context);
        return Task.CompletedTask;
    }

    private static void Collect(DbContext? context)
    {
        if (context == null)
            return;

        var userIds = context.ChangeTracker.Entries()
                             .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                             .Select(e => e.Entity switch
                             {
                                 UserDeckPreference p => p.UserId,
                                 UserMediaListEntry m => m.UserId,
                                 UserProfile p => p.UserId,
                                 UserAccomplishment a => a.UserId,
                                 UserPinnedDeck p => p.UserId,
                                 UserFavouriteWord w => w.UserId,
                                 _ => null
                             })
                             .OfType<string>()
                             .ToHashSet();
        if (userIds.Count > 0)
            Pending.AddOrUpdate(context, userIds);
    }

    private static HashSet<string>? Take(DbContext? context) =>
        context != null && Pending.TryGetValue(context, out var userIds) && Pending.Remove(context) ? userIds : null;
}
