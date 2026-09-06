using Jiten.Core.Data.YouTube;

namespace Jiten.Core.YouTube;

/// <summary>
/// Everything known about one video after a fetch attempt: the ledger verdict plus, when accepted, the
/// cleaned subtitle file the parse pipeline reads.
/// </summary>
public class YouTubeFetchOutcome
{
    public required YouTubeVideoStatus Status { get; init; }
    public string? SkipReason { get; init; }
    public YouTubeVideoInfo? Info { get; init; }
    public string? CleanedSrtPath { get; init; }
    public YouTubeSubtitleCleanResult? Cleaned { get; init; }

    public bool Accepted => Status == YouTubeVideoStatus.Fetched && CleanedSrtPath != null;
}

/// <summary>
/// Fetch, clean and policy-check one video. Shared by the server fetch job and the home CLI so both produce
/// identical ledger verdicts.
/// </summary>
public class YouTubeVideoFetcher(YtDlpClient client)
{
    public async Task<YouTubeFetchOutcome> FetchAsync(string videoId, string listedTitle, int? listedRuntimeSeconds,
                                                      string workDirectory, YouTubeSourceFilters filters,
                                                      CancellationToken cancellationToken = default)
    {
        var titleReason = YouTubeContentPolicy.CheckTitle(listedTitle, filters.TitleInclude, filters.TitleExclude)
                          ?? YouTubeContentPolicy.CheckRuntime(listedRuntimeSeconds, filters);
        if (titleReason != null)
            return new YouTubeFetchOutcome { Status = YouTubeVideoStatus.FilteredOut, SkipReason = titleReason };

        var videoDirectory = Path.Combine(workDirectory, videoId);
        var fetch = await client.FetchVideoAsync(videoId, videoDirectory, cancellationToken);

        if (!fetch.Succeeded)
            return new YouTubeFetchOutcome { Status = fetch.Status, SkipReason = fetch.SkipReason, Info = fetch.Info };

        var info = fetch.Info!;

        // The listing was already filtered, but the fetched title is the canonical one
        titleReason = YouTubeContentPolicy.CheckTitle(info.Title, filters.TitleInclude, filters.TitleExclude)
                      ?? YouTubeContentPolicy.CheckRuntime(info.DurationSeconds, filters);
        if (titleReason != null)
            return new YouTubeFetchOutcome { Status = YouTubeVideoStatus.FilteredOut, SkipReason = titleReason, Info = info };

        if (info.IsLive || info.LiveStatus is "is_live" or "is_upcoming")
            return new YouTubeFetchOutcome { Status = YouTubeVideoStatus.FilteredOut, SkipReason = "not-accessible: not-yet-available", Info = info };

        var cleanedPath = Path.Combine(videoDirectory, $"{info.VideoId}.clean.srt");
        var cleaned = await YouTubeSubtitleCleaner.CleanFileAsync(info.SubtitlePath!, cleanedPath);

        var densityReason = YouTubeContentPolicy.CheckDensity(cleaned, info.DurationSeconds);
        if (densityReason != null)
            return new YouTubeFetchOutcome { Status = YouTubeVideoStatus.FilteredOut, SkipReason = densityReason, Info = info, Cleaned = cleaned };

        return new YouTubeFetchOutcome
        {
            Status = YouTubeVideoStatus.Fetched,
            Info = info,
            CleanedSrtPath = cleanedPath,
            Cleaned = cleaned
        };
    }
}
