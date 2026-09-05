namespace Jiten.Api.Services;

public interface IStudySessionService
{
    Task<string> CreateSession(string userId);
    Task<bool> ValidateSession(string sessionId, string userId);
    /// <summary>Scope is a session id, or a per-user scope for clients that review without a session.</summary>
    Task<string?> GetCachedReviewResult(string scope, string clientRequestId);
    Task StoreCachedReviewResult(string scope, string clientRequestId, string resultJson);
    Task RefreshSession(string sessionId);
    Task<long> BumpStudyOverviewVersion(string userId);
    Task<long> GetStudyOverviewVersion(string userId);
}
