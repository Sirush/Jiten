using StackExchange.Redis;

namespace Jiten.Api.Helpers;

/// <summary>Flags a download once its account or IP has pulled more distinct decks this UTC day than a person plausibly would; flagged downloads still succeed but stay out of download stats.</summary>
public static class BulkDownloadDetector
{
    public const int DailyDistinctDecks = 20;

    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(48);

    public static string SubjectFor(string? userId, HttpContext http) =>
        userId != null ? $"user:{userId}" : $"ip:{VisitorKeyHelper.For(http)}";

    /// <summary>Fails open: when Redis is unreachable the download counts as a normal one.</summary>
    public static async Task<bool> IsBulkAsync(IConnectionMultiplexer redis, string subject, int deckId, DateTime utcNow, ILogger logger)
    {
        var key = $"deckdl:{utcNow:yyyyMMdd}:{subject}";
        try
        {
            var db = redis.GetDatabase();
            if (await db.SetAddAsync(key, deckId))
                await db.KeyExpireAsync(key, KeyLifetime);
            return await db.SetLengthAsync(key) > DailyDistinctDecks;
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Bulk download check skipped");
            return false;
        }
    }
}
