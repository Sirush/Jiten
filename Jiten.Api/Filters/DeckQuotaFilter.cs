using System.Globalization;
using System.Security.Claims;
using Jiten.Api.Telemetry;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using StackExchange.Redis;

namespace Jiten.Api.Helpers;

/// <summary>Same SSR and trusted-IP checks as the rate-limit policies, which only Program.cs can evaluate.</summary>
public sealed record DeckQuotaExemption(Func<HttpContext, bool> IsExempt);

/// <summary>
/// Caps the distinct decks one account or IP can download or page through per UTC day, so a bulk clone stalls
/// while re-downloading or paging an already counted deck stays free.
/// </summary>
public sealed class DeckQuotaFilter(IConnectionMultiplexer redis, DeckQuotaExemption exemption, ILogger<DeckQuotaFilter> logger)
    : IAsyncActionFilter
{
    public const int AnonymousDailyDecks = 50;
    public const int SignedInDailyDecks = 100;

    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(48);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var tier = http.User.FindFirst("rate_limit_tier")?.Value;
        if (tier is "Researcher" or "Unlimited" || exemption.IsExempt(http) ||
            !context.ActionArguments.TryGetValue("id", out var idArgument) || idArgument is not int deckId)
        {
            await next();
            return;
        }

        var userId = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var now = DateTime.UtcNow;
        var subject = userId != null ? $"user:{userId}" : $"ip:{VisitorKeyHelper.For(http)}";
        var limit = userId != null ? SignedInDailyDecks : AnonymousDailyDecks;

        if (!await ExceedsQuota($"deckquota:{now:yyyyMMdd}:{subject}", deckId, limit))
        {
            await next();
            return;
        }

        RateLimitTelemetry.Rejected.Add(1, new KeyValuePair<string, object?>("policy", "deck-quota"),
                                        new KeyValuePair<string, object?>("partition", userId != null ? "user" : "ip"));

        var untilReset = now.Date.AddDays(1) - now;
        http.Response.Headers.RetryAfter = ((int)Math.Ceiling(untilReset.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        context.Result = new ContentResult
                         {
                             StatusCode = StatusCodes.Status429TooManyRequests, ContentType = "text/plain",
                             Content = userId != null
                                 ? $"You've reached today's limit of {SignedInDailyDecks} decks. Try again after midnight UTC."
                                 : $"You've reached today's limit of {AnonymousDailyDecks} decks. Sign in to raise it to {SignedInDailyDecks}, or try again after midnight UTC."
                         };
    }

    /// <summary>Fails open when Redis is unreachable; an outage must not block every deck download.</summary>
    private async Task<bool> ExceedsQuota(string key, int deckId, int limit)
    {
        try
        {
            var db = redis.GetDatabase();
            if (!await db.SetAddAsync(key, deckId))
                return false;

            await db.KeyExpireAsync(key, KeyLifetime);
            if (await db.SetLengthAsync(key) <= limit)
                return false;

            await db.SetRemoveAsync(key, deckId);
            return true;
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Deck quota check skipped");
            return false;
        }
    }
}
