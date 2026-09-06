using Jiten.Core.Data.YouTube;
using Jiten.Core.YouTube;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Jiten.Cli.Commands;

/// <summary>
/// Home-connection side of the YouTube pipeline. Fetching runs here when the server's egress is bot-checked;
/// parsing always happens server-side once rows reach the Fetched state.
/// </summary>
public class YouTubeCommands(CliContext context)
{
    /// <summary>
    /// Dry run: resolves the source, lists its videos, fetches the first N and reports every verdict. Writes
    /// only to the staging directory, never to the database.
    /// </summary>
    public async Task Test(CliOptions options)
    {
        var client = CreateClient();
        var fetcher = new YouTubeVideoFetcher(client);
        var stagingDirectory = StagingDirectory(options);

        var source = await ResolveAndReport(client, options.YtTest!, options.YtMax);
        if (source == null)
            return;

        var delay = DelayBetweenVideos();
        var outcomes = new List<(YouTubeVideoListing Listing, YouTubeFetchOutcome Outcome)>();

        var listings = source.Videos.ToDictionary(v => v.VideoId);

        Console.WriteLine();
        foreach (var chunk in source.Videos.Chunk(fetcher.BatchSize))
        {
            var requests = chunk.Select(v => new YouTubeFetchRequest(v.VideoId, v.Title, v.DurationSeconds)).ToList();
            var batch = await fetcher.FetchManyAsync(requests, stagingDirectory, Filters(options));

            foreach (var (videoId, outcome) in batch.Outcomes)
            {
                var listing = listings[videoId];
                outcomes.Add((listing, outcome));

                if (outcome.Accepted)
                {
                    var info = outcome.Info!;
                    var cleaned = outcome.Cleaned!;
                    Console.WriteLine($"  accept {listing.VideoId}  {cleaned.CharacterCount,6} chars  {info.DurationSeconds / 60.0,5:0.0} min" +
                                      $"  readings-{cleaned.DroppedReadingLines} latin-{cleaned.DroppedLatinLines}" +
                                      $"  {info.UploadedAt:yyyy-MM-dd}  {Preview(info.Title, 50)}");
                }
                else
                {
                    Console.WriteLine($"  skip   {listing.VideoId}  {outcome.SkipReason}  {Preview(outcome.Info?.Title ?? listing.Title, 50)}");
                }
            }

            if (batch.BlockedMessage != null)
            {
                Console.WriteLine($"  STOP   {batch.BlockedMessage}");
                Console.WriteLine("  This IP is bot-checked. Switch egress (YtDlp:Proxy) or run from a home connection.");
                break;
            }

            await Task.Delay(delay);
        }

        Console.WriteLine();
        Console.WriteLine($"{outcomes.Count} videos checked:");
        foreach (var group in outcomes.GroupBy(o => o.Outcome.Accepted ? "accepted" : Prefix(o.Outcome.SkipReason))
                                      .OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Count(),5}  {group.Key}");
        }

        var accepted = outcomes.Where(o => o.Outcome.Accepted).ToList();
        if (accepted.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine("Sample of cleaned lines:");
        foreach (var (listing, outcome) in accepted.Take(3))
        {
            Console.WriteLine($"  [{listing.VideoId}] {Preview(outcome.Info!.Title, 60)}");
            foreach (var cue in outcome.Cleaned!.Cues.Take(6))
                Console.WriteLine($"    {TimeSpan.FromMilliseconds(cue.StartMs):hh\\:mm\\:ss}  {string.Join(" / ", cue.Lines)}");
        }

        Console.WriteLine();
        Console.WriteLine($"Staged files kept in {stagingDirectory}");
    }

    /// <summary>
    /// Registers a channel or playlist as a parent deck, seeds its ledger from the full listing and drains it.
    /// The server's import job parses the fetched subdecks on its next run.
    /// </summary>
    public async Task Import(CliOptions options)
    {
        var client = CreateClient();

        var source = await ResolveAndReport(client, options.YtImport!, options.YtMax);
        if (source == null)
            return;

        source.OldestUploadAt = await client.GetOldestUploadDateAsync(source);
        Console.WriteLine($"  Oldest video: {source.OldestUploadAt:yyyy-MM-dd}");

        int deckId;
        var ingest = CreateIngestClient();
        if (ingest != null)
        {
            try
            {
                deckId = await ingest.RegisterSourceAsync(source, Filters(options));
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Registration refused: {ex.Message}");
                return;
            }
        }
        else
        {
            var registrar = new YouTubeSourceRegistrar(context.ContextFactory);
            var conflict = await registrar.CheckConflictsAsync(source);
            if (conflict != null)
            {
                Console.WriteLine(conflict);
                return;
            }

            var cover = await client.DownloadImageAsync(source.CoverUrl) ?? [];
            deckId = await registrar.RegisterAsync(source, Filters(options), cover);
        }

        Console.WriteLine($"Registered deck {deckId} '{source.Title}' with {source.Videos.Count} pending videos.");

        if (ingest != null)
            await DrainRemote(ingest, deckId, options);
        else
            await DrainSource(deckId, options);
    }

    /// <summary>
    /// Completes a registration the admin parked on the dashboard: resolves the channel here, hands the listing
    /// to the API (which applies the titles, date, cover and filters typed there), then drains it.
    /// </summary>
    public async Task Register(CliOptions options)
    {
        var ingest = CreateIngestClient();
        if (ingest == null)
        {
            Console.WriteLine("--yt-register needs YouTube:ApiBaseUrl and YouTube:IngestKey in the configuration.");
            return;
        }

        YouTubeIngestClient.Registration registration;
        try
        {
            registration = await ingest.GetRegistrationAsync(options.YtRegister!.Value);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Registration {options.YtRegister} not available: {ex.Message}");
            return;
        }

        var client = CreateClient();
        var source = await ResolveAndReport(client, registration.Url, null);
        if (source == null)
            return;

        if (!registration.HasReleaseDate)
        {
            source.OldestUploadAt = await client.GetOldestUploadDateAsync(source);
            Console.WriteLine($"  Oldest video: {source.OldestUploadAt:yyyy-MM-dd}");
        }

        int deckId;
        try
        {
            deckId = await ingest.CompleteRegistrationAsync(registration.Id, source);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Registration refused: {ex.Message}");
            return;
        }

        Console.WriteLine($"Registered deck {deckId} '{source.Title}' with {source.Videos.Count} pending videos.");
        await DrainRemote(ingest, deckId, options);
    }

    /// <summary>
    /// Full re-enumeration of a tracked source from this machine: seeds the videos the feed or a truncated
    /// first listing never showed, then drains them.
    /// </summary>
    public async Task Bootstrap(CliOptions options)
    {
        var ingest = CreateIngestClient();
        if (ingest == null)
        {
            Console.WriteLine("--yt-bootstrap needs YouTube:ApiBaseUrl and YouTube:IngestKey in the configuration.");
            return;
        }

        var deckId = options.YtBootstrap!.Value;
        YouTubeIngestClient.TrackedSource tracked;
        try
        {
            tracked = await ingest.GetSourceAsync(deckId);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Deck {deckId} is not a tracked YouTube source: {ex.Message}");
            return;
        }

        var source = await ResolveAndReport(CreateClient(), tracked.Url, null);
        if (source == null)
            return;

        try
        {
            var (listed, added) = await ingest.BootstrapAsync(deckId, source);
            Console.WriteLine($"Deck {deckId}: {listed} videos listed, {added} new pending.");
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Bootstrap refused: {ex.Message}");
            return;
        }

        await DrainRemote(ingest, deckId, options);
    }

    /// <summary>
    /// Drains Pending ledger rows for one source (deck id) or every source ("all") from this machine's egress.
    /// </summary>
    public async Task Drain(CliOptions options)
    {
        var ingest = CreateIngestClient();
        if (ingest != null)
        {
            var all = options.YtDrain!.Equals("all", StringComparison.OrdinalIgnoreCase);
            if (!all && !int.TryParse(options.YtDrain, out _))
            {
                Console.WriteLine("--yt-drain takes a parent deck id or 'all'.");
                return;
            }

            await DrainRemote(ingest, all ? null : int.Parse(options.YtDrain!), options);
            return;
        }

        List<int> deckIds;
        if (options.YtDrain!.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            await using var db = await context.ContextFactory.CreateDbContextAsync();
            deckIds = await db.YouTubeVideos
                              .Where(v => v.Status == YouTubeVideoStatus.Pending)
                              .Select(v => v.SourceDeckId)
                              .Distinct()
                              .ToListAsync();
            Console.WriteLine($"{deckIds.Count} sources have pending videos.");
        }
        else if (int.TryParse(options.YtDrain, out var single))
        {
            deckIds = [single];
        }
        else
        {
            Console.WriteLine("--yt-drain takes a parent deck id or 'all'.");
            return;
        }

        foreach (var deckId in deckIds)
        {
            var result = await DrainSource(deckId, options);
            if (result.Blocked)
                break;
        }
    }

    private async Task<YouTubeDrainResult> DrainSource(int deckId, CliOptions options)
    {
        var service = new YouTubeDrainService(context.ContextFactory, CreateClient(),
                                              async items =>
                                              {
                                                  var stats = await Jiten.Parser.SubtitleMoraRateCalculator.ComputeAsync(items);
                                                  return (stats.DurationMs, stats.MoraCount);
                                              },
                                              StagingDirectory(options), DelayBetweenVideos());

        Console.WriteLine($"Draining deck {deckId}...");
        var result = await service.DrainAsync(deckId, options.YtMax ?? int.MaxValue);

        Console.WriteLine($"  checked {result.Checked}, fetched {result.Fetched}, skipped {result.Skipped}");
        if (result.Blocked)
            Console.WriteLine($"  STOP   {result.Error}");
        else if (result.Error != null)
            Console.WriteLine($"  {result.Error}");
        else if (result.Fetched > 0)
            Console.WriteLine("  Fetched subdecks are parsed by the server's YouTube import job on its next run.");

        return result;
    }

    /// <summary>
    /// Fetches with this machine's yt-dlp and hands every verdict to the API, which owns the database.
    /// </summary>
    private async Task DrainRemote(YouTubeIngestClient ingest, int? deckId, CliOptions options)
    {
        var fetcher = new YouTubeVideoFetcher(CreateClient());
        var stagingDirectory = StagingDirectory(options);
        var delay = DelayBetweenVideos();

        List<YouTubeIngestClient.PendingSource> sources;
        try
        {
            sources = await ingest.GetPendingAsync(deckId, options.YtMax ?? 500);
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"Could not list pending videos: {ex.Message}");
            return;
        }

        if (sources.Count == 0)
        {
            Console.WriteLine("Nothing pending.");
            return;
        }

        foreach (var source in sources)
        {
            Console.WriteLine($"Draining deck {source.DeckId} '{source.ChannelName}' ({source.Videos.Count} pending)...");
            int fetched = 0, skipped = 0;

            var videos = source.Videos.ToDictionary(v => v.VideoId);
            var workDirectory = Path.Combine(stagingDirectory, source.DeckId.ToString());
            var blocked = false;

            foreach (var chunk in source.Videos.Chunk(fetcher.BatchSize))
            {
                var requests = chunk.Select(v => new YouTubeFetchRequest(v.VideoId, v.Title, v.RuntimeSeconds)).ToList();
                var batch = await fetcher.FetchManyAsync(requests, workDirectory, source.Filters);

                foreach (var (videoId, outcome) in batch.Outcomes)
                {
                    var video = videos[videoId];
                    if (outcome.FetchFailed)
                    {
                        Console.WriteLine($"  error  {videoId}  {Preview(outcome.SkipReason ?? "", 120)}");
                        skipped++;
                        continue;
                    }

                    try
                    {
                        if (outcome.Accepted)
                        {
                            var childDeckId = await ingest.UploadFetchedAsync(source.DeckId, videoId, outcome);
                            fetched++;
                            Console.WriteLine($"  fetched {videoId}  deck {childDeckId}  {outcome.Cleaned!.CharacterCount,6} chars  {Preview(outcome.Info!.Title, 50)}");
                        }
                        else
                        {
                            await ingest.SkipAsync(source.DeckId, videoId, outcome);
                            skipped++;
                            Console.WriteLine($"  skip    {videoId}  {outcome.SkipReason}  {Preview(outcome.Info?.Title ?? video.Title, 50)}");
                        }
                    }
                    catch (HttpRequestException ex)
                    {
                        Console.WriteLine($"  upload failed for {videoId}: {Preview(ex.Message, 200)}");
                    }
                }

                if (batch.BlockedMessage != null)
                {
                    Console.WriteLine($"  STOP   {batch.BlockedMessage}");
                    blocked = true;
                    break;
                }

                await Task.Delay(delay);
            }

            if (fetched > 0)
            {
                try
                {
                    await ingest.ImportFetchedAsync(source.DeckId);
                }
                catch (HttpRequestException ex)
                {
                    Console.WriteLine($"  parse request failed: {Preview(ex.Message, 200)} (the hourly pass picks the rows up)");
                }
            }

            Console.WriteLine($"  fetched {fetched}, skipped {skipped}.{(fetched > 0 ? " Parsing queued on the server." : "")}");
            if (blocked)
                return;
        }
    }

    /// <summary>Null when YouTube:ApiBaseUrl is unset, in which case the CLI writes to the database directly.</summary>
    private YouTubeIngestClient? CreateIngestClient()
    {
        var baseUrl = context.Configuration["YouTube:ApiBaseUrl"];
        var key = context.Configuration["YouTube:IngestKey"];
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("YouTube:ApiBaseUrl is set but YouTube:IngestKey is missing.");

        Console.WriteLine($"Ingesting through {baseUrl}");
        return new YouTubeIngestClient(baseUrl, key);
    }

    private async Task<YouTubeSourceInfo?> ResolveAndReport(YtDlpClient client, string input, int? maxVideos)
    {
        if (!YouTubeUrlParser.TryParse(input, out _, out var listingUrl, out _))
        {
            Console.WriteLine($"'{input}' is not a YouTube channel or playlist URL.");
            return null;
        }

        Console.WriteLine($"Listing {listingUrl}...");
        YouTubeSourceInfo source;
        try
        {
            source = await client.ResolveSourceAsync(input, maxVideos);
        }
        catch (Exception ex) when (ex is YtDlpFailedException or YtDlpBlockedException)
        {
            Console.WriteLine($"  {ex.Message}");
            return null;
        }

        Console.WriteLine($"  Kind        : {source.Kind}");
        Console.WriteLine($"  Source id   : {source.SourceId}");
        Console.WriteLine($"  Title       : {source.Title}");
        Console.WriteLine($"  Channel     : {source.ChannelName} ({source.ChannelId})");
        Console.WriteLine($"  Cover       : {source.CoverUrl}");
        Console.WriteLine($"  Description : {Preview(source.Description, 100)}");
        Console.WriteLine($"  Videos      : {source.Videos.Count}{(maxVideos is > 0 ? $" (capped at {maxVideos})" : "")}");
        return source;
    }

    private YtDlpClient CreateClient()
    {
        var ytDlpOptions = new YtDlpOptions();
        context.Configuration.GetSection("YtDlp").Bind(ytDlpOptions);

        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Jiten/1.0");
        http.Timeout = TimeSpan.FromSeconds(30);

        return new YtDlpClient(ytDlpOptions, http);
    }

    private static YouTubeSourceFilters Filters(CliOptions options) =>
        new(options.YtInclude, options.YtExclude,
            options.YtMinMinutes is > 0 ? (int)(options.YtMinMinutes.Value * 60) : null,
            options.YtMaxMinutes is > 0 ? (int)(options.YtMaxMinutes.Value * 60) : null);

    private TimeSpan DelayBetweenVideos() => TimeSpan.FromMilliseconds(context.Configuration.GetValue("YtDlp:DelayMs", 1500));

    private string StagingDirectory(CliOptions options)
    {
        var root = options.YtStaging
                   ?? Path.Combine(context.Configuration["StaticFilesPath"] ?? Path.GetTempPath(), "tmp", "youtube");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Prefix(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
            return "unknown";
        var colon = reason.IndexOf(':');
        return colon > 0 ? reason[..colon] : reason;
    }

    private static string Preview(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        var single = value.Replace('\n', ' ');
        return single.Length <= maxLength ? single : single[..maxLength] + "…";
    }
}
