using System.Text.Json;
using Jiten.Api.Dtos;
using Jiten.Api.Helpers;
using Jiten.Core;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services.Notices;

public record NoticeContext(string UserId, UserDbContext UserContext, IFrequencySourceResolver FrequencySource);

public record OneTimeNotice(
    string Key,
    string Surface,
    DateTime ExpiresAtUtc,
    Func<NoticeContext, Task<IReadOnlyDictionary<string, string>?>> Evaluate,
    bool Granted = false);

public record ActiveNoticeDto(string Key, IReadOnlyDictionary<string, string> Values);

/// <summary>One row per notice; its copy lives in Jiten.Web/app/utils/oneTimeNotices.ts under the same key.</summary>
public class OneTimeNoticeRegistry(IReadOnlyList<OneTimeNotice> notices)
{
    public IReadOnlyList<OneTimeNotice> Notices { get; } = notices;

    public static OneTimeNoticeRegistry Default { get; } = new(
    [
        // Granted by migration AddOneTimeNotices to users whose decks reorder on deploy.
        new OneTimeNotice("study-order-follows-rank-source", "decks", new DateTime(2026, 12, 10, 0, 0, 0, DateTimeKind.Utc),
                          StudyOrderFollowsRankSource, Granted: true),
    ]);

    /// <summary>Still affected: a default rank source that resolves to non-global and an active deck whose frequency order follows it.</summary>
    private static async Task<IReadOnlyDictionary<string, string>?> StudyOrderFollowsRankSource(NoticeContext ctx)
    {
        var scope = await ctx.FrequencySource.Resolve(ctx.UserId);
        if (scope.IsGlobal) return null;

        var affected = await ctx.UserContext.UserStudyDecks.AsNoTracking()
                                .AnyAsync(sd => sd.UserId == ctx.UserId && sd.IsActive
                                                && sd.Order == (int)DeckOrder.GlobalFrequency
                                                && (sd.DeckType == StudyDeckType.MediaDeck
                                                    || sd.DeckType == StudyDeckType.MediaGroup
                                                    || sd.DeckType == StudyDeckType.StaticWordList));
        if (!affected) return null;

        if (scope.MediaType is { } mediaType)
            return new Dictionary<string, string> { ["mediaType"] = ((int)mediaType).ToString() };

        var listName = await ctx.UserContext.UserFrequencyLists.AsNoTracking()
                                .Where(f => f.Id == scope.FrequencyListId)
                                .Select(f => f.Name)
                                .FirstOrDefaultAsync();
        return new Dictionary<string, string> { ["listName"] = listName ?? "" };
    }
}

public interface IOneTimeNoticeService
{
    Task<List<ActiveNoticeDto>> GetActive(string userId, string surface, DateTime utcNow);

    /// <summary>False when the key names no registered notice.</summary>
    Task<bool> Dismiss(string userId, string key);
}

public class OneTimeNoticeService(
    UserDbContext userContext,
    IFrequencySourceResolver frequencySource,
    OneTimeNoticeRegistry registry) : IOneTimeNoticeService
{
    public async Task<List<ActiveNoticeDto>> GetActive(string userId, string surface, DateTime utcNow)
    {
        var candidates = registry.Notices.Where(n => n.Surface == surface && utcNow < n.ExpiresAtUtc).ToList();
        if (candidates.Count == 0) return [];

        var stored = await userContext.UserSettings.AsNoTracking()
                                      .Where(us => us.UserId == userId)
                                      .Select(us => new { us.DismissedNoticesJson, us.GrantedNoticesJson })
                                      .FirstOrDefaultAsync();
        var dismissed = ParseKeys(stored?.DismissedNoticesJson);
        var granted = ParseKeys(stored?.GrantedNoticesJson);

        var ctx = new NoticeContext(userId, userContext, frequencySource);
        var active = new List<ActiveNoticeDto>();
        foreach (var notice in candidates.Where(n => !dismissed.Contains(n.Key) && (!n.Granted || granted.Contains(n.Key))))
        {
            var values = await notice.Evaluate(ctx);
            if (values != null) active.Add(new ActiveNoticeDto(notice.Key, values));
        }

        return active;
    }

    public async Task<bool> Dismiss(string userId, string key)
    {
        if (registry.Notices.All(n => n.Key != key)) return false;

        await using var transaction = await userContext.Database.BeginTransactionAsync();
        var settings = await UserSettingsRows.LoadForUpdate(userContext, userId);
        var dismissed = ParseKeys(settings.DismissedNoticesJson);
        if (dismissed.Add(key))
        {
            settings.DismissedNoticesJson = JsonSerializer.Serialize(dismissed.Order(StringComparer.Ordinal));
            await userContext.SaveChangesAsync();
        }

        await transaction.CommitAsync();
        return true;
    }

    /// <summary>A malformed document reads as "nothing dismissed", so the worst case is a notice shown again.</summary>
    private static HashSet<string> ParseKeys(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<HashSet<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
