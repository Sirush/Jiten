namespace Jiten.Api.Services;

public enum ReviewClaimStatus { Acquired, InFlight, Completed }

/// <summary>ResultJson is set only for <see cref="ReviewClaimStatus.Completed"/>.</summary>
public readonly record struct ReviewClaim(ReviewClaimStatus Status, string? ResultJson = null)
{
    public static readonly ReviewClaim Acquired = new(ReviewClaimStatus.Acquired);
    public static readonly ReviewClaim InFlight = new(ReviewClaimStatus.InFlight);
}

public interface IStudySessionService
{
    Task<string> CreateSession(string userId);
    Task<bool> ValidateSession(string sessionId, string userId);
    /// <summary>Scope is a session id, or a per-user scope for clients that review without a session.</summary>
    Task<ReviewClaim> TryClaimReview(string scope, string clientRequestId);
    /// <summary>Frees a claim whose review did not commit, so a retry can run instead of seeing it in flight.</summary>
    Task ReleaseReviewClaim(string scope, string clientRequestId);
    Task StoreCachedReviewResult(string scope, string clientRequestId, string resultJson);
    Task RefreshSession(string sessionId);
    Task<long> BumpStudyOverviewVersion(string userId);
    Task<long> GetStudyOverviewVersion(string userId);

    Task StoreNewCardCursorHints(string userId, IReadOnlyDictionary<long, int> nextDeckByWordKey);
    Task<int?> TakeNewCardCursorHint(string userId, long wordKey);
}
