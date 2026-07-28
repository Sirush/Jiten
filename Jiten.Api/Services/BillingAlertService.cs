using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services;

public interface IBillingAlertService
{
    /// <summary>
    /// Logs at Error and pushes an out-of-band notification. Best-effort: never throws, so a billing path can
    /// call it without risking the transaction it is reporting on. <paramref name="key"/> identifies the alert
    /// class and is what the cooldown is keyed on.
    /// </summary>
    Task RaiseAsync(string key, string title, string detail, CancellationToken ct = default);
}

public class BillingAlertService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IMemoryCache cache,
    ILogger<BillingAlertService> logger) : IBillingAlertService
{
    // Stripe replays a failing event on its own retry schedule, so the same fault re-alerts every few minutes.
    // The Error log is always written; only the push is collapsed to one per key per window.
    private static readonly TimeSpan PushCooldown = TimeSpan.FromMinutes(15);

    private const int MaxDetailLength = 1500;

    public async Task RaiseAsync(string key, string title, string detail, CancellationToken ct = default)
    {
        logger.LogError("BILLING ALERT [{AlertKey}] {Title}: {Detail}", key, title, detail);

        var webhook = configuration["DiscordWebhook"];
        if (string.IsNullOrEmpty(webhook))
            return;

        var cacheKey = $"billing:alert:{key}";
        if (cache.TryGetValue(cacheKey, out _))
            return;

        cache.Set(cacheKey, true, PushCooldown);

        try
        {
            if (detail.Length > MaxDetailLength)
                detail = detail[..MaxDetailLength] + "…";

            var payload = JsonSerializer.Serialize(new
            {
                content = $"**Billing alert — {title}**\n```\n{detail}\n```",
                username = "BillingAlert",
                tts = false
            });

            using var client = httpClientFactory.CreateClient();
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(webhook, content, ct);

            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Billing alert push returned {Status} for {AlertKey}", (int)response.StatusCode, key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Billing alert push failed for {AlertKey}", key);
        }
    }
}
