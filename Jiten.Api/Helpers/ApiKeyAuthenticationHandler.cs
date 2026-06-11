using System.Security.Claims;
using System.Text.Encodings.Web;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Jiten.Api.Authentication;

public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    UserDbContext userDb,
    UserManager<User> userManager,
    ApiKeyService apiKeyService)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    // Track invalid authentication attempts per client IP to prevent brute-forcing of API keys.
    private static readonly ConcurrentDictionary<string, FailedAttemptTracker> FailedAttemptLimiters = new();

    // Max invalid attempts per IP in the configured window before rejecting with 429.
    private const int FAILED_ATTEMPT_LIMIT = 5;
    private static readonly TimeSpan FAILED_WINDOW = TimeSpan.FromMinutes(1);

    // Optional small delay added on failed authentication to slow down brute-force attempts.
    private const int FAILED_DELAY_MIN_MS = 150;
    private const int FAILED_DELAY_MAX_MS = 350;

    // Evict idle per-IP limiters (each holds a replenishment timer) so the dictionary can't grow forever.
    private static readonly TimeSpan LIMITER_IDLE_EVICTION = TimeSpan.FromMinutes(10);
    private const int LIMITER_SWEEP_THRESHOLD = 1000;

    // Hashes of keys we already know are invalid; retries of the same bad key skip the database.
    private static readonly ConcurrentDictionary<string, DateTime> InvalidKeyHashes = new();
    private static readonly TimeSpan INVALID_KEY_TTL = TimeSpan.FromMinutes(2);
    private const int INVALID_KEY_CACHE_MAX = 10_000;

    // Throttle LastUsedAt writes to one per key per interval instead of one per request.
    private static readonly ConcurrentDictionary<int, DateTime> LastUsedWrites = new();
    private static readonly TimeSpan LAST_USED_WRITE_INTERVAL = TimeSpan.FromMinutes(1);

    private sealed class FailedAttemptTracker(FixedWindowRateLimiter limiter)
    {
        public FixedWindowRateLimiter Limiter { get; } = limiter;
        public DateTime LastAttempt = DateTime.UtcNow;
    }


    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var apiKey = ExtractApiKey();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AuthenticateResult.NoResult();
        }

        try
        {
            var ip = Context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // Reject already-throttled IPs before doing any hashing or database work.
            if (FailedAttemptLimiters.TryGetValue(ip, out var tracker))
            {
                using var probe = tracker.Limiter.AttemptAcquire(0);
                if (!probe.IsAcquired)
                {
                    tracker.LastAttempt = DateTime.UtcNow;
                    return RejectTooManyAttempts();
                }
            }

            var hash = apiKeyService.ComputeHash(apiKey);

            // Retries of a key we already know is invalid skip the database entirely.
            if (InvalidKeyHashes.TryGetValue(hash, out var invalidUntil) && invalidUntil > DateTime.UtcNow)
            {
                return await HandleFailedAttemptAsync(ip);
            }

            var apiKeyWithUser = await userDb.ApiKeys
                                             .Include(k => k.User)
                                             .FirstOrDefaultAsync(k => k.Hash == hash &&
                                                                       !k.IsRevoked &&
                                                                       (k.ExpiresAt == null || k.ExpiresAt > DateTime.UtcNow));

            if (apiKeyWithUser?.User == null)
            {
                CacheInvalidHash(hash);
                return await HandleFailedAttemptAsync(ip);
            }

            var user = apiKeyWithUser.User;

            // Check if user is active/not locked
            if (await userManager.IsLockedOutAsync(user))
            {
                return AuthenticateResult.Fail("User account is locked");
            }

            QueueLastUsedUpdate(apiKeyWithUser.Id);

            var claims = await BuildClaims(user, apiKeyWithUser);
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "API key authentication error");
            return AuthenticateResult.Fail("Authentication error");
        }
    }

    private async Task<AuthenticateResult> HandleFailedAttemptAsync(string ip)
    {
        Logger.LogWarning("Invalid API key attempt from {IP}", ip);

        var tracker = GetOrAddTracker(ip);
        tracker.LastAttempt = DateTime.UtcNow;

        using var lease = tracker.Limiter.AttemptAcquire(1);
        if (!lease.IsAcquired)
        {
            return RejectTooManyAttempts();
        }

        // Small random delay to reduce brute-force speed even under the limit
        try
        {
            await Task.Delay(Random.Shared.Next(FAILED_DELAY_MIN_MS, FAILED_DELAY_MAX_MS));
        }
        catch
        {
        }

        return AuthenticateResult.Fail("Invalid API key");
    }

    private AuthenticateResult RejectTooManyAttempts()
    {
        Context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        Context.Response.Headers["Retry-After"] = ((int)FAILED_WINDOW.TotalSeconds).ToString();
        return AuthenticateResult.Fail("Too many invalid authentication attempts");
    }

    private static FailedAttemptTracker GetOrAddTracker(string ip)
    {
        if (FailedAttemptLimiters.Count > LIMITER_SWEEP_THRESHOLD)
        {
            var cutoff = DateTime.UtcNow - LIMITER_IDLE_EVICTION;
            foreach (var (key, value) in FailedAttemptLimiters)
            {
                if (value.LastAttempt < cutoff && FailedAttemptLimiters.TryRemove(key, out var removed))
                {
                    removed.Limiter.Dispose();
                }
            }
        }

        return FailedAttemptLimiters.GetOrAdd(ip, _ => new FailedAttemptTracker(
                                                  new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
                                                                             {
                                                                                 PermitLimit = FAILED_ATTEMPT_LIMIT,
                                                                                 Window = FAILED_WINDOW,
                                                                                 QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                                                                                 QueueLimit = 0, AutoReplenishment = true
                                                                             })));
    }

    private static void CacheInvalidHash(string hash)
    {
        if (InvalidKeyHashes.Count >= INVALID_KEY_CACHE_MAX)
        {
            var now = DateTime.UtcNow;
            foreach (var (key, expiry) in InvalidKeyHashes)
            {
                if (expiry <= now)
                {
                    InvalidKeyHashes.TryRemove(key, out _);
                }
            }

            // Under sustained pressure from unique bad keys, drop the cache rather than grow it.
            if (InvalidKeyHashes.Count >= INVALID_KEY_CACHE_MAX)
            {
                InvalidKeyHashes.Clear();
            }
        }

        InvalidKeyHashes[hash] = DateTime.UtcNow + INVALID_KEY_TTL;
    }

    private void QueueLastUsedUpdate(int apiKeyId)
    {
        var now = DateTime.UtcNow;
        var lastWrite = LastUsedWrites.GetOrAdd(apiKeyId, DateTime.MinValue);
        if (now - lastWrite < LAST_USED_WRITE_INTERVAL || !LastUsedWrites.TryUpdate(apiKeyId, now, lastWrite))
        {
            return;
        }

        // Resolve the root scope factory now; Context.RequestServices is disposed once the request ends.
        var scopeFactory = Context.RequestServices.GetRequiredService<IServiceScopeFactory>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<UserDbContext>();
                await dbContext.ApiKeys
                               .Where(k => k.Id == apiKeyId)
                               .ExecuteUpdateAsync(setters => setters.SetProperty(k => k.LastUsedAt, now));
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to update API key last used timestamp");
            }
        });
    }

    private string? ExtractApiKey()
    {
        // Check custom header first
        if (Request.Headers.TryGetValue(Options.HeaderName, out var headerValues))
        {
            var apiKey = headerValues.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(apiKey))
                return apiKey;
        }

        // Check Authorization header
        if (Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            var value = authHeader.FirstOrDefault();
            if (!string.IsNullOrEmpty(value))
            {
                // Support multiple formats: "ApiKey xxx", "Bearer xxx", "xxx"
                if (value.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
                {
                    return value.Substring("ApiKey ".Length).Trim();
                }

                if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return value.Substring("Bearer ".Length).Trim();
                }

                if (!value.Contains(' '))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private async Task<List<Claim>> BuildClaims(User user, ApiKey apiKey)
    {
        var claims = new List<Claim>
                     {
                         new(ClaimTypes.NameIdentifier, user.Id), new(ClaimTypes.Name, user.UserName ?? string.Empty),
                         new(ClaimTypes.Email, user.Email ?? string.Empty), new("amr", "api_key"), new("auth_scheme", "ApiKey"),
                         new("api_key_id", apiKey.Id.ToString()),
                         new("rate_limit_tier", user.RateLimitTier.ToString())
                     };

        var roles = await userManager.GetRolesAsync(user);
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return claims;
    }
}