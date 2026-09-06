namespace Jiten.Api.Jobs;

/// <summary>
/// Single-worker queue: every yt-dlp call from the server serialises here so the politeness delay holds
/// across sources.
/// </summary>
public static class YouTubeQueues
{
    public const string Fetch = "youtube-fetch";
}
