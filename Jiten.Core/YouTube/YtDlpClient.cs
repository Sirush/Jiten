using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Jiten.Core.Data.YouTube;

namespace Jiten.Core.YouTube;

public class YtDlpOptions
{
    public string Path { get; set; } = "yt-dlp";

    /// <summary>Forwarded as --proxy; unset means the machine's own egress</summary>
    public string? Proxy { get; set; }

    public int TimeoutSeconds { get; set; } = 180;
}

/// <summary>
/// Raised when YouTube refuses the egress IP itself rather than one video; a drain must stop, not mark videos.
/// </summary>
public class YtDlpBlockedException(string message) : Exception(message);

public class YtDlpFailedException(string message) : Exception(message);

/// <summary>
/// Process wrapper around the yt-dlp executable. Metadata and subtitle text only; never downloads media.
/// </summary>
public class YtDlpClient(YtDlpOptions options, HttpClient httpClient)
{
    // Named tracks are keyed "ja-<trackId>", so the language filter is a regex and the written file is globbed
    private const string JapaneseTrackPattern = "ja.*";

    public async Task<YouTubeSourceInfo> ResolveSourceAsync(string input, int? maxVideos = null,
                                                            CancellationToken cancellationToken = default)
    {
        if (!YouTubeUrlParser.TryParse(input, out var kind, out var listingUrl, out _))
            throw new ArgumentException($"'{input}' is not a YouTube channel or playlist URL.");

        var args = new List<string> { "--flat-playlist", "-J", "--no-warnings" };
        if (maxVideos is > 0)
            args.AddRange(["--playlist-items", $"1:{maxVideos}"]);
        args.Add(listingUrl);

        var (stdout, _) = await RunAsync(args, cancellationToken);

        using var document = JsonDocument.Parse(stdout);
        var root = document.RootElement;

        var info = new YouTubeSourceInfo
        {
            Kind = kind,
            ChannelName = GetString(root, "channel") ?? GetString(root, "uploader") ?? string.Empty,
            ChannelId = GetString(root, "channel_id"),
            Description = GetString(root, "description")
        };

        if (kind == YouTubeSourceKind.Channel)
        {
            info.SourceId = info.ChannelId ?? throw new YtDlpFailedException($"yt-dlp returned no channel_id for {listingUrl}");
            info.Title = info.ChannelName;
            info.CoverUrl = PickThumbnail(root, preferId: "avatar_uncropped");
        }
        else
        {
            info.SourceId = GetString(root, "id") ?? throw new YtDlpFailedException($"yt-dlp returned no playlist id for {listingUrl}");
            info.Title = GetString(root, "title") ?? info.SourceId;
            info.CoverUrl = PickThumbnail(root, preferId: null);
        }

        if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            var position = 0;
            foreach (var entry in entries.EnumerateArray())
            {
                var id = GetString(entry, "id");
                if (string.IsNullOrEmpty(id))
                    continue;

                info.Videos.Add(new YouTubeVideoListing(id,
                                                        GetString(entry, "title") ?? id,
                                                        GetInt(entry, "duration"),
                                                        position++));
            }
        }

        return info;
    }

    /// <summary>
    /// Fetches one video's metadata and, when a manual Japanese track exists, writes it into
    /// <paramref name="outputDirectory"/> as {id}.{lang}.srt (or .vtt when YouTube offers no srt).
    /// </summary>
    public async Task<YouTubeFetchResult> FetchVideoAsync(string videoId, string outputDirectory,
                                                          CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);

        var args = new List<string>
        {
            "--skip-download", "--no-playlist", "--no-warnings", "--no-progress", "--ignore-no-formats-error",
            "--write-subs", "--no-write-auto-subs",
            "--sub-langs", JapaneseTrackPattern,
            "--sub-format", "srt/vtt/best",
            "--print-json",
            "-o", System.IO.Path.Combine(outputDirectory, "%(id)s.%(ext)s"),
            YouTubeUrlParser.VideoUrl(videoId)
        };

        string stdout;
        try
        {
            (stdout, _) = await RunAsync(args, cancellationToken);
        }
        catch (YtDlpFailedException ex)
        {
            var classified = ClassifyVideoError(ex.Message);
            if (classified != null)
                return YouTubeFetchResult.Skip(classified.Value.Status, classified.Value.Reason);
            throw;
        }

        var jsonStart = stdout.IndexOf('{');
        if (jsonStart < 0)
            throw new YtDlpFailedException($"yt-dlp printed no JSON for {videoId}");

        using var document = JsonDocument.Parse(stdout[jsonStart..]);
        var root = document.RootElement;

        var info = new YouTubeVideoInfo
        {
            VideoId = GetString(root, "id") ?? videoId,
            Title = GetString(root, "title") ?? videoId,
            Description = GetString(root, "description"),
            DurationSeconds = GetInt(root, "duration"),
            UploadedAt = ParseUploadedAt(root),
            PlayableInEmbed = !root.TryGetProperty("playable_in_embed", out var embed) || embed.ValueKind != JsonValueKind.False,
            Availability = GetString(root, "availability"),
            LiveStatus = GetString(root, "live_status"),
            IsLive = root.TryGetProperty("is_live", out var live) && live.ValueKind == JsonValueKind.True,
            ThumbnailUrl = GetString(root, "thumbnail"),
            ChannelId = GetString(root, "channel_id"),
            ChannelName = GetString(root, "channel") ?? GetString(root, "uploader"),
            ManualSubtitleLanguages = PropertyNames(root, "subtitles"),
            AutomaticCaptionLanguages = PropertyNames(root, "automatic_captions")
        };

        info.SubtitlePath = Directory.EnumerateFiles(outputDirectory, $"{info.VideoId}.ja*.*")
                                     .Where(f => f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) ||
                                                 f.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase))
                                     .OrderByDescending(f => new FileInfo(f).Length)
                                     .FirstOrDefault();

        var hasJapaneseManual = info.ManualSubtitleLanguages.Any(IsJapaneseTrack);
        if (info.SubtitlePath == null)
        {
            if (hasJapaneseManual)
                return YouTubeFetchResult.Skip(YouTubeVideoStatus.NoManualSubs, "fetch-error: ja track listed but not written", info);

            var hasJapaneseAsr = info.AutomaticCaptionLanguages.Any(IsJapaneseTrack);
            return YouTubeFetchResult.Skip(YouTubeVideoStatus.NoManualSubs, hasJapaneseAsr ? "asr-only" : "no-ja-track", info);
        }

        return new YouTubeFetchResult { Info = info, Status = YouTubeVideoStatus.Fetched };
    }

    /// <summary>
    /// Upload date of the last listed video (channels list newest first). One metadata call; null on any failure.
    /// </summary>
    public async Task<DateTimeOffset?> GetOldestUploadDateAsync(YouTubeSourceInfo source, CancellationToken cancellationToken = default)
    {
        var last = source.Videos.LastOrDefault();
        if (last == null)
            return null;

        try
        {
            var args = new List<string> { "-j", "--no-playlist", "--no-warnings", "--ignore-no-formats-error", YouTubeUrlParser.VideoUrl(last.VideoId) };
            var (stdout, _) = await RunAsync(args, cancellationToken);
            var jsonStart = stdout.IndexOf('{');
            if (jsonStart < 0)
                return null;

            using var document = JsonDocument.Parse(stdout[jsonStart..]);
            return ParseUploadedAt(document.RootElement);
        }
        catch (Exception ex) when (ex is YtDlpFailedException or YtDlpBlockedException or JsonException)
        {
            return null;
        }
    }

    private const int MaxImageBytes = 8 * 1024 * 1024;

    /// <summary>Hosts YouTube serves thumbnails and avatars from; anything else is refused so an uploaded URL cannot reach internal services.</summary>
    public static bool IsYouTubeImageUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.EndsWith(".ytimg.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".ggpht.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase));

    public async Task<byte[]?> DownloadImageAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (!IsYouTubeImageUrl(url))
            return null;

        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxImageBytes)
                return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length <= MaxImageBytes ? bytes : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// maxresdefault only exists for videos uploaded in HD; hqdefault always does.
    /// </summary>
    public async Task<byte[]?> DownloadVideoThumbnailAsync(string videoId, string? preferredUrl,
                                                          CancellationToken cancellationToken = default)
    {
        return await DownloadImageAsync(preferredUrl, cancellationToken)
               ?? await DownloadImageAsync($"https://i.ytimg.com/vi/{videoId}/maxresdefault.jpg", cancellationToken)
               ?? await DownloadImageAsync($"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg", cancellationToken);
    }

    public static bool IsJapaneseTrack(string language) =>
        language.Equals("ja", StringComparison.OrdinalIgnoreCase) ||
        language.StartsWith("ja-", StringComparison.OrdinalIgnoreCase);

    private async Task<(string Stdout, string Stderr)> RunAsync(List<string> args, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // yt-dlp otherwise writes in the console code page and Japanese error text arrives garbled
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        // Without this YouTube serves auto-translated titles and descriptions in the machine's locale
        startInfo.ArgumentList.Add("--extractor-args");
        startInfo.ArgumentList.Add("youtube:lang=ja");

        if (!string.IsNullOrEmpty(options.Proxy))
        {
            startInfo.ArgumentList.Add("--proxy");
            startInfo.ArgumentList.Add(options.Proxy);
        }

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new YtDlpFailedException($"Could not start '{options.Path}': {ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new YtDlpFailedException($"yt-dlp timed out after {options.TimeoutSeconds}s: {string.Join(' ', args)}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (stderr.Contains("confirm you're not a bot", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("confirm you’re not a bot", StringComparison.OrdinalIgnoreCase))
        {
            throw new YtDlpBlockedException("YouTube bot check on this IP: " + FirstErrorLine(stderr));
        }

        if (process.ExitCode != 0)
            throw new YtDlpFailedException(FirstErrorLine(stderr));

        return (stdout, stderr);
    }

    private static string FirstErrorLine(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim())
                         .FirstOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
        return line ?? stderr.Trim().Split('\n').LastOrDefault()?.Trim() ?? "yt-dlp failed";
    }

    private static (YouTubeVideoStatus Status, string Reason)? ClassifyVideoError(string error)
    {
        var e = error.ToLowerInvariant();
        if (e.Contains("private video"))
            return (YouTubeVideoStatus.Dead, "not-accessible: private");
        if (e.Contains("video unavailable") || e.Contains("has been removed") || e.Contains("no longer available") ||
            e.Contains("account associated with this video has been terminated"))
            return (YouTubeVideoStatus.Dead, "not-accessible: removed");
        if (e.Contains("members-only") || e.Contains("join this channel"))
            return (YouTubeVideoStatus.FilteredOut, "not-accessible: members-only");
        if (e.Contains("confirm your age") || e.Contains("age-restricted") || e.Contains("inappropriate for some users"))
            return (YouTubeVideoStatus.FilteredOut, "not-accessible: age-gated");
        if (e.Contains("not available in your country") || e.Contains("geo"))
            return (YouTubeVideoStatus.FilteredOut, "not-accessible: geo-blocked");
        if (e.Contains("premieres in") || e.Contains("is not yet available") || e.Contains("live event will begin"))
            return (YouTubeVideoStatus.FilteredOut, "not-accessible: not-yet-available");
        return null;
    }

    private static string? PickThumbnail(JsonElement root, string? preferId)
    {
        if (!root.TryGetProperty("thumbnails", out var thumbnails) || thumbnails.ValueKind != JsonValueKind.Array)
            return null;

        string? last = null;
        foreach (var thumbnail in thumbnails.EnumerateArray())
        {
            var url = GetString(thumbnail, "url");
            if (string.IsNullOrEmpty(url))
                continue;

            if (preferId != null && GetString(thumbnail, "id") == preferId)
                return url;

            last = url;
        }

        return last;
    }

    private static DateTimeOffset? ParseUploadedAt(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var timestamp) && timestamp.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.FromUnixTimeSeconds(timestamp.GetInt64());

        var uploadDate = GetString(root, "upload_date");
        if (uploadDate != null &&
            DateTime.TryParseExact(uploadDate, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            return new DateTimeOffset(date, TimeSpan.Zero);

        return null;
    }

    private static List<string> PropertyNames(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.Object)
            return [];
        return element.EnumerateObject().Select(p => p.Name).ToList();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;
        return value.TryGetInt32(out var i) ? i : (int)Math.Round(value.GetDouble());
    }
}
