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

    /// <summary>yt-dlp failed on the video itself; the ledger row keeps its Pending status and is retried later</summary>
    public bool FetchFailed => Status == YouTubeVideoStatus.Pending;
}

public record YouTubeFetchRequest(string VideoId, string ListedTitle, int? ListedRuntimeSeconds);

public class YouTubeBatchFetchOutcome
{
    /// <summary>In request order; videos yt-dlp never reached because of a bot check are absent</summary>
    public List<(string VideoId, YouTubeFetchOutcome Outcome)> Outcomes { get; } = new();

    public string? BlockedMessage { get; set; }
}

/// <summary>
/// Fetch, clean and policy-check videos. Shared by the server fetch job and the home CLI so both produce
/// identical ledger verdicts.
/// </summary>
public class YouTubeVideoFetcher(YtDlpClient client)
{
    public int BatchSize => client.BatchSize;

    public async Task<YouTubeFetchOutcome> FetchAsync(string videoId, string listedTitle, int? listedRuntimeSeconds,
                                                      string workDirectory, YouTubeSourceFilters filters,
                                                      CancellationToken cancellationToken = default)
    {
        var batch = await FetchManyAsync([new YouTubeFetchRequest(videoId, listedTitle, listedRuntimeSeconds)],
                                         workDirectory, filters, cancellationToken);
        if (batch.BlockedMessage != null)
            throw new YtDlpBlockedException(batch.BlockedMessage);

        var outcome = batch.Outcomes[0].Outcome;
        if (outcome.FetchFailed)
            throw new YtDlpFailedException(outcome.SkipReason ?? "yt-dlp failed");
        return outcome;
    }

    /// <summary>
    /// Title and runtime filters are applied from the listing first; only the survivors go to one yt-dlp process.
    /// </summary>
    public async Task<YouTubeBatchFetchOutcome> FetchManyAsync(IReadOnlyList<YouTubeFetchRequest> requests, string workDirectory,
                                                               YouTubeSourceFilters filters, CancellationToken cancellationToken = default)
    {
        var batch = new YouTubeBatchFetchOutcome();
        var prefiltered = new Dictionary<string, YouTubeFetchOutcome>();
        var toFetch = new List<string>();

        foreach (var request in requests)
        {
            var titleReason = YouTubeContentPolicy.CheckTitle(request.ListedTitle, filters.TitleInclude, filters.TitleExclude)
                              ?? YouTubeContentPolicy.CheckRuntime(request.ListedRuntimeSeconds, filters);
            if (titleReason != null)
                prefiltered[request.VideoId] = new YouTubeFetchOutcome { Status = YouTubeVideoStatus.FilteredOut, SkipReason = titleReason };
            else
                toFetch.Add(request.VideoId);
        }

        var fetched = await client.FetchVideosAsync(toFetch, workDirectory, cancellationToken);
        batch.BlockedMessage = fetched.BlockedMessage;

        foreach (var request in requests)
        {
            if (prefiltered.TryGetValue(request.VideoId, out var outcome))
                batch.Outcomes.Add((request.VideoId, outcome));
            else if (fetched.Results.TryGetValue(request.VideoId, out var result))
                batch.Outcomes.Add((request.VideoId, await Judge(result, workDirectory, filters)));
        }

        return batch;
    }

    private static async Task<YouTubeFetchOutcome> Judge(YouTubeFetchResult fetch, string workDirectory, YouTubeSourceFilters filters)
    {
        if (fetch.Error != null)
            return new YouTubeFetchOutcome { Status = YouTubeVideoStatus.Pending, SkipReason = $"fetch-error: {fetch.Error}" };

        if (!fetch.Succeeded)
            return new YouTubeFetchOutcome { Status = fetch.Status, SkipReason = fetch.SkipReason, Info = fetch.Info };

        var info = fetch.Info!;

        // The listing was already filtered, but the fetched title is the canonical one
        var titleReason = YouTubeContentPolicy.CheckTitle(info.Title, filters.TitleInclude, filters.TitleExclude)
                          ?? YouTubeContentPolicy.CheckRuntime(info.DurationSeconds, filters);
        if (titleReason != null)
            return new YouTubeFetchOutcome { Status = YouTubeVideoStatus.FilteredOut, SkipReason = titleReason, Info = info };

        if (info.IsLive || info.LiveStatus is "is_live" or "is_upcoming")
            return new YouTubeFetchOutcome { Status = YouTubeVideoStatus.FilteredOut, SkipReason = "not-accessible: not-yet-available", Info = info };

        var cleanedPath = Path.Combine(workDirectory, $"{info.VideoId}.clean.srt");
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
