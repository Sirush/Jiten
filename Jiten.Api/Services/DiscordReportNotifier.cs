using System.Text;
using System.Text.Json;

namespace Jiten.Api.Services;

public interface IDiscordReportNotifier
{
    /// <summary>Posts a moderation report embed to the Discord webhook. Returns false when unconfigured or the push fails; never throws.</summary>
    Task<bool> SendAsync(string reporterId, string title, string description, string username = "IssueReporter", CancellationToken ct = default);
}

public class DiscordReportNotifier(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<DiscordReportNotifier> logger) : IDiscordReportNotifier
{
    private const int EmbedColor = 8266731;

    public async Task<bool> SendAsync(string reporterId, string title, string description, string username = "IssueReporter",
                                      CancellationToken ct = default)
    {
        var webhook = configuration["DiscordWebhook"];
        if (string.IsNullOrEmpty(webhook))
            return false;

        var payload = JsonSerializer.Serialize(new
        {
            content = $"A new report from user ID `{reporterId}` came in.\n",
            tts = false,
            embeds = new[] { new { title, description, color = EmbedColor } },
            username
        });

        try
        {
            using var client = httpClientFactory.CreateClient();
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(webhook, content, ct);
            if (response.IsSuccessStatusCode)
                return true;

            logger.LogWarning("Discord report push returned {Status} for {Title}", (int)response.StatusCode, title);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Discord report push failed for {Title}", title);
        }

        return false;
    }
}
