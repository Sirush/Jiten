namespace Jiten.Api.Helpers;

public static class RetryAfterHeader
{
    /// <summary>Accepts both the delta and the HTTP-date form; null when absent or already past.</summary>
    public static TimeSpan? Read(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter == null)
            return null;

        var wait = retryAfter.Delta ?? (retryAfter.Date.HasValue ? retryAfter.Date.Value - DateTimeOffset.UtcNow : null);
        return wait is { } value && value > TimeSpan.Zero ? value : null;
    }
}
