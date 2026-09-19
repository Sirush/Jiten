using System.Collections.Concurrent;
using Jiten.Api.Services;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

public class InMemoryStudySessionService : IStudySessionService
{
    private readonly ConcurrentDictionary<string, string> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _reviews = new();

    public Task<string> CreateSession(string userId)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        _sessions[sessionId] = userId;
        return Task.FromResult(sessionId);
    }

    public Task<bool> ValidateSession(string sessionId, string userId)
    {
        if (!_sessions.TryGetValue(sessionId, out var owner)) return Task.FromResult(true);
        return Task.FromResult(owner == userId);
    }

    public Task<string?> GetCachedReviewResult(string sessionId, string clientRequestId)
    {
        _reviews.TryGetValue($"{sessionId}:{clientRequestId}", out var result);
        return Task.FromResult(result);
    }

    public Task StoreCachedReviewResult(string sessionId, string clientRequestId, string resultJson)
    {
        _reviews[$"{sessionId}:{clientRequestId}"] = resultJson;
        return Task.CompletedTask;
    }

    public Task RefreshSession(string sessionId) => Task.CompletedTask;

    private readonly ConcurrentDictionary<string, long> _versions = new();

    public Task<long> BumpStudyOverviewVersion(string userId)
    {
        var version = _versions.AddOrUpdate(userId, 1, (_, v) => v + 1);
        return Task.FromResult(version);
    }

    public Task<long> GetStudyOverviewVersion(string userId)
    {
        return Task.FromResult(_versions.GetValueOrDefault(userId, 0));
    }

    private readonly ConcurrentDictionary<string, int> _cursorHints = new();

    public Task StoreNewCardCursorHints(string userId, IReadOnlyDictionary<long, int> nextDeckByWordKey)
    {
        foreach (var (wordKey, deckId) in nextDeckByWordKey)
            _cursorHints[$"{userId}:{wordKey}"] = deckId;
        return Task.CompletedTask;
    }

    public Task<int?> TakeNewCardCursorHint(string userId, long wordKey)
    {
        return Task.FromResult(_cursorHints.TryRemove($"{userId}:{wordKey}", out var deckId) ? deckId : (int?)null);
    }
}
