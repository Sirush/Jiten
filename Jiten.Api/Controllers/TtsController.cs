using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Jiten.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Jiten.Api.Controllers;

[ApiController]
[Route("api/tts")]
[AllowAnonymous]
[EnableRateLimiting("fixed")]
public class TtsController(ITtsService ttsService, IConfiguration configuration) : ControllerBase
{
    [HttpGet("word/{wordId:int}/{readingIndex:int}")]
    public async Task<IResult> GetWordAudio(int wordId, int readingIndex, [FromQuery] string voice = "female", CancellationToken ct = default)
    {
        var rateLimitKey = GetRateLimitKey();
        try
        {
            var audio = await ttsService.GetWordAudioAsync(wordId, readingIndex, voice, rateLimitKey, ct, IsTrustedInternal());
            return Results.File(audio, "audio/ogg");
        }
        catch (TtsTextNotFoundException)
        {
            return Results.NotFound(new { error = "Word not found" });
        }
        catch (TtsGenerationLimitException)
        {
            return Results.StatusCode(429);
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(503);
        }
        catch (TaskCanceledException)
        {
            return Results.StatusCode(504);
        }
    }

    [HttpGet("sentence/{sentenceId:int}")]
    public async Task<IResult> GetSentenceAudio(int sentenceId, [FromQuery] string voice = "female", CancellationToken ct = default)
    {
        var rateLimitKey = GetRateLimitKey();
        try
        {
            var audio = await ttsService.GetSentenceAudioAsync(sentenceId, voice, rateLimitKey, ct);
            return Results.File(audio, "audio/ogg");
        }
        catch (TtsTextNotFoundException)
        {
            return Results.NotFound(new { error = "Sentence not found" });
        }
        catch (TtsGenerationLimitException)
        {
            return Results.StatusCode(429);
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(503);
        }
        catch (TaskCanceledException)
        {
            return Results.StatusCode(504);
        }
    }

    [HttpGet("custom-sentence/{id:int}")]
    public async Task<IResult> GetCustomSentenceAudio(int id, [FromQuery] string voice = "female", CancellationToken ct = default)
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();

        var rateLimitKey = GetRateLimitKey();
        try
        {
            var audio = await ttsService.GetCustomSentenceAudioAsync(id, userId, voice, rateLimitKey, ct);
            return Results.File(audio, "audio/ogg");
        }
        catch (TtsTextNotFoundException)
        {
            return Results.NotFound(new { error = "Sentence not found" });
        }
        catch (TtsGenerationLimitException)
        {
            return Results.StatusCode(429);
        }
        catch (HttpRequestException)
        {
            return Results.StatusCode(503);
        }
        catch (TaskCanceledException)
        {
            return Results.StatusCode(504);
        }
    }

    private string GetRateLimitKey()
    {
        var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return userId ?? GetClientIp(HttpContext);
    }

    private bool IsTrustedInternal()
    {
        var key = configuration["SsrBypassKey"];
        if (string.IsNullOrEmpty(key)) return false;
        if (!Request.Headers.TryGetValue("X-Internal-Ssr-Key", out var provided)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided.ToString()), Encoding.UTF8.GetBytes(key));
    }

    private static string GetClientIp(HttpContext context)
    {
        foreach (var header in new[] { "X-Forwarded-For", "X-Real-IP", "CF-Connecting-IP" })
        {
            var value = context.Request.Headers[header].FirstOrDefault();
            if (string.IsNullOrEmpty(value)) continue;
            var ip = value.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(ip) && ip != "unknown") return ip;
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
