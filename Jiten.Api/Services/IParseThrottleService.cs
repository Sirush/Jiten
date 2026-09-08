namespace Jiten.Api.Services;

public interface IParseThrottleService
{
    bool TryConsume(string userId, int characterCount, out TimeSpan retryAfter);
}
