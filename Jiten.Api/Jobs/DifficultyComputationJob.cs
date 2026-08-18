using Hangfire;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace Jiten.Api.Jobs;

public class DifficultyComputationJob(
    IDbContextFactory<JitenDbContext> contextFactory,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    IBackgroundJobClient backgroundJobs,
    ILogger<DifficultyComputationJob> logger)
{
    private record DifficultyResponse(
        [property: JsonPropertyName("difficulty")] decimal? Difficulty,
        [property: JsonPropertyName("baseline")] decimal? Baseline,
        [property: JsonPropertyName("peak")] decimal? Peak,
        [property: JsonPropertyName("deciles")] Dictionary<string, decimal>? Deciles,
        [property: JsonPropertyName("progression")] List<ProgressionItem>? Progression,
        [property: JsonPropertyName("sentences")] int Sentences,
        [property: JsonPropertyName("level_counts")] Dictionary<string, int>? LevelCounts);

    private record ProgressionItem(
        [property: JsonPropertyName("segment")] int Segment,
        [property: JsonPropertyName("difficulty")] decimal? Difficulty,
        [property: JsonPropertyName("peak")] decimal? Peak);

    private record RunPodInput(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("batch_size")] int BatchSize = 64,
        [property: JsonPropertyName("sample_size")] int SampleSize = 150,
        [property: JsonPropertyName("num_segments")] int NumSegments = 10);

    private record RunPodRequest([property: JsonPropertyName("input")] RunPodInput Input);

    private record RunPodRunResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] string Status);

    private record RunPodStatusResponse(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("output")] DifficultyResponse? Output,
        [property: JsonPropertyName("error")] string? Error);

    private const int MaxPayloadChars = 1_500_000;
    private const int MaxConcurrentApiCalls = 5;
    private static readonly SemaphoreSlim ApiThrottle = new(MaxConcurrentApiCalls);
    private static readonly char[] SentenceEndings = ['。', '！', '？', '」', '』', '）', '\n'];

    [Queue("stats")]
    [AutomaticRetry(Attempts = 1)]
    public async Task ReaggregateParentDifficulty(int deckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var deck = await context.Decks
            .Include(d => d.Children)
            .FirstOrDefaultAsync(d => d.DeckId == deckId);

        if (deck == null)
        {
            logger.LogWarning("Deck {DeckId} not found for difficulty reaggregation", deckId);
            return;
        }

        if (deck.Children.Count == 0)
        {
            logger.LogWarning("Deck {DeckId} has no children, skipping reaggregation", deckId);
            return;
        }

        var childrenWithDifficulty = await context.Decks
            .Include(d => d.DeckDifficulty)
            .Where(d => d.ParentDeckId == deckId)
            .OrderBy(d => d.DeckOrder)
            .ToListAsync();

        await AggregateParentDifficulty(context, deck, childrenWithDifficulty);
        logger.LogInformation("Reaggregated parent deck {DeckId} difficulty from {ChildCount} children", deckId, childrenWithDifficulty.Count);
    }

    [Queue("stats")]
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [60, 300, 900])]
    public async Task ComputeDeckDifficulty(int deckId, bool forceRecompute = false)
    {
        await using var context = await contextFactory.CreateDbContextAsync();

        var deck = await context.Decks
            .Include(d => d.RawText)
            .Include(d => d.Children).ThenInclude(c => c.RawText)
            .FirstOrDefaultAsync(d => d.DeckId == deckId);

        if (deck == null)
        {
            logger.LogWarning("Deck {DeckId} not found for difficulty computation", deckId);
            return;
        }

        // If parent with children, compute all children first then aggregate
        if (deck.Children.Count > 0)
        {
            await ComputeParentWithChildren(context, deck, forceRecompute);
            return;
        }

        // Single deck (no children) - compute via API
        if (!forceRecompute && await context.DeckDifficulties.AnyAsync(d => d.DeckId == deckId))
        {
            // logger.LogInformation("Skipping deck {DeckId} - difficulty already computed", deckId);
            return;
        }

        await ComputeSingleDeckDifficulty(context, deck);
    }

    private async Task ComputeParentWithChildren(JitenDbContext context, Deck parent, bool forceRecompute = false)
    {
        var children = await context.Decks
            .Include(d => d.RawText)
            .Include(d => d.DeckDifficulty)
            .Where(d => d.ParentDeckId == parent.DeckId)
            .OrderBy(d => d.DeckOrder)
            .ToListAsync();

        logger.LogInformation("Computing difficulty for parent {ParentId} with {Count} children (forceRecompute={Force})",
            parent.DeckId, children.Count, forceRecompute);

        var computedCount = 0;
        var failedChildren = new List<int>();
        foreach (var child in children)
        {
            if (!forceRecompute && child.DeckDifficulty != null)
            {
                // logger.LogInformation("Skipping child {DeckId} — difficulty already computed", child.DeckId);
                continue;
            }

            if (child.RawText == null)
            {
                logger.LogWarning("Child deck {DeckId} has no raw text, skipping", child.DeckId);
                continue;
            }

            try
            {
                await ComputeSingleDeckDifficulty(context, child);
                computedCount++;
                logger.LogInformation("Computed child {Current}/{Total}: deck {DeckId}",
                    computedCount, children.Count, child.DeckId);
            }
            catch (Exception ex)
            {
                // Usually a transient RunPod worker failure (e.g. CUDA "no kernel image"). Re-enqueue the child
                // below as its own job so it inherits the per-deck delayed-retry policy and lands on a fresh worker.
                logger.LogError(ex, "Failed to compute difficulty for child deck {DeckId}, will re-enqueue", child.DeckId);
                failedChildren.Add(child.DeckId);
            }
        }

        // Reload children with their DeckDifficulty (fresh query to ensure DeckDifficulty is loaded)
        var childrenWithDifficulty = await context.Decks
            .Include(d => d.DeckDifficulty)
            .Where(d => d.ParentDeckId == parent.DeckId)
            .OrderBy(d => d.DeckOrder)
            .ToListAsync();

        // Aggregate parent difficulty from whatever succeeded so the parent is never blocked by a few failures
        await AggregateParentDifficulty(context, parent, childrenWithDifficulty);

        // Self-heal stragglers: re-enqueue each failed child (leaf → 3× delayed retry) and schedule a single
        // delayed re-aggregation so the parent picks up the recovered children once their retries complete.
        if (failedChildren.Count > 0)
        {
            logger.LogWarning("Re-enqueueing {Count} failed children for parent {ParentId}: {ChildIds}",
                failedChildren.Count, parent.DeckId, string.Join(",", failedChildren));

            foreach (var childId in failedChildren)
                backgroundJobs.Enqueue<DifficultyComputationJob>(j => j.ComputeDeckDifficulty(childId, false));

            backgroundJobs.Schedule<DifficultyComputationJob>(
                j => j.ReaggregateParentDifficulty(parent.DeckId), TimeSpan.FromMinutes(45));
        }
    }

    private async Task ComputeSingleDeckDifficulty(JitenDbContext context, Deck deck)
    {
        if (deck.RawText == null)
        {
            logger.LogWarning("Deck {DeckId} has no raw text for difficulty computation", deck.DeckId);
            return;
        }

        var mediaType = GetApiMediaType(deck.MediaType);
        var response = await CallRunPodApi(deck.RawText.RawText, mediaType);

        if (response == null || !response.Difficulty.HasValue)
        {
            throw new InvalidOperationException($"Failed to get difficulty for deck {deck.DeckId} - API call failed or returned null difficulty");
        }

        var roundedDeciles = (response.Deciles ?? new()).ToDictionary(
            kvp => kvp.Key,
            kvp => Math.Round(kvp.Value, 2));

        var roundedProgression = (response.Progression ?? [])
            .Where(p => p.Difficulty.HasValue && p.Peak.HasValue)
            .Select(p => new ProgressionSegment
            {
                Segment = p.Segment,
                Difficulty = Math.Round(p.Difficulty!.Value, 2),
                Peak = Math.Round(p.Peak!.Value, 2)
            }).ToList();

        var existingDifficulty = await context.DeckDifficulties.FindAsync(deck.DeckId);
        if (existingDifficulty != null)
        {
            existingDifficulty.Difficulty = Math.Round(response.Difficulty.Value, 2);
            existingDifficulty.Peak = Math.Round(response.Peak ?? response.Difficulty.Value, 2);
            existingDifficulty.Deciles = roundedDeciles;
            existingDifficulty.Progression = roundedProgression;
            existingDifficulty.LastUpdated = DateTimeOffset.UtcNow;
        }
        else
        {
            var newDifficulty = new DeckDifficulty
            {
                DeckId = deck.DeckId,
                Difficulty = Math.Round(response.Difficulty.Value, 2),
                Peak = Math.Round(response.Peak ?? response.Difficulty.Value, 2),
                LastUpdated = DateTimeOffset.UtcNow
            };
            newDifficulty.Deciles = roundedDeciles;
            newDifficulty.Progression = roundedProgression;
            await context.DeckDifficulties.AddAsync(newDifficulty);
        }

        deck.Difficulty = (float)Math.Round(response.Difficulty.Value, 2);
        await context.SaveChangesAsync();
    }

    private async Task AggregateParentDifficulty(JitenDbContext context, Deck parent, List<Deck> children)
    {
        var childrenWithDifficulty = children
            .Where(c => c.DeckDifficulty != null)
            .ToList();

        if (childrenWithDifficulty.Count == 0)
        {
            logger.LogWarning("Parent deck {DeckId} has no children with difficulty computed", parent.DeckId);
            return;
        }

        var avgDifficulty = childrenWithDifficulty.Average(c => c.DeckDifficulty!.Difficulty);
        var maxPeak = childrenWithDifficulty.Max(c => c.DeckDifficulty!.Peak);

        var progression = ComputeParentProgression(childrenWithDifficulty);
        var aggregatedDeciles = ComputeAggregatedDeciles(childrenWithDifficulty);

        var existingDifficulty = await context.DeckDifficulties.FindAsync(parent.DeckId);
        if (existingDifficulty != null)
        {
            existingDifficulty.Difficulty = Math.Round(avgDifficulty, 2);
            existingDifficulty.Peak = Math.Round(maxPeak, 2);
            existingDifficulty.Deciles = aggregatedDeciles;
            existingDifficulty.Progression = progression;
            existingDifficulty.LastUpdated = DateTimeOffset.UtcNow;
        }
        else
        {
            var newDifficulty = new DeckDifficulty
            {
                DeckId = parent.DeckId,
                Difficulty = Math.Round(avgDifficulty, 2),
                Peak = Math.Round(maxPeak, 2),
                LastUpdated = DateTimeOffset.UtcNow
            };
            newDifficulty.Deciles = aggregatedDeciles;
            newDifficulty.Progression = progression;
            await context.DeckDifficulties.AddAsync(newDifficulty);
        }

        parent.Difficulty = (float)Math.Round(avgDifficulty, 2);
        await context.SaveChangesAsync();

        logger.LogInformation("Aggregated parent deck {DeckId} difficulty to {Difficulty} with {SegmentCount} progression segments (from {Count} children)",
            parent.DeckId, avgDifficulty, progression.Count, childrenWithDifficulty.Count);
    }

    private static List<ProgressionSegment> ComputeParentProgression(List<Deck> childrenWithDifficulty)
    {
        var segments = new List<ProgressionSegment>();
        var childCount = childrenWithDifficulty.Count;

        if (childCount == 0)
            return segments;

        const int targetSegments = 25;
        var childrenPerSegment = (int)Math.Ceiling(childCount / (double)targetSegments);

        for (var segmentIndex = 0; segmentIndex < targetSegments; segmentIndex++)
        {
            var startIdx = segmentIndex * childrenPerSegment;
            if (startIdx >= childCount)
                break;

            var endIdx = Math.Min((segmentIndex + 1) * childrenPerSegment, childCount) - 1;
            var childrenInSegment = childrenWithDifficulty
                .Skip(startIdx)
                .Take(endIdx - startIdx + 1)
                .ToList();

            if (childrenInSegment.Count == 0)
                continue;

            var segmentDifficulty = childrenInSegment.Average(c => c.DeckDifficulty!.Difficulty);
            var segmentPeak = childrenInSegment.Max(c => c.DeckDifficulty!.Peak);

            segments.Add(new ProgressionSegment
            {
                Segment = segmentIndex + 1,
                Difficulty = Math.Round(segmentDifficulty, 2),
                Peak = Math.Round(segmentPeak, 2),
                ChildStartOrder = childrenInSegment.First().DeckOrder,
                ChildEndOrder = childrenInSegment.Last().DeckOrder
            });
        }

        return segments;
    }

    private static Dictionary<string, decimal> ComputeAggregatedDeciles(List<Deck> childrenWithDifficulty)
    {
        var aggregatedDeciles = new Dictionary<string, decimal>();

        // Collect all unique decile keys from children
        var allKeys = childrenWithDifficulty
            .Where(c => c.DeckDifficulty?.Deciles != null)
            .SelectMany(c => c.DeckDifficulty!.Deciles.Keys)
            .Distinct()
            .ToList();

        foreach (var key in allKeys)
        {
            var values = childrenWithDifficulty
                .Where(c => c.DeckDifficulty?.Deciles.ContainsKey(key) == true)
                .Select(c => c.DeckDifficulty!.Deciles[key])
                .ToList();

            if (values.Count > 0)
            {
                aggregatedDeciles[key] = Math.Round(values.Average(), 2);
            }
        }

        return aggregatedDeciles;
    }

    private async Task<DifficultyResponse?> CallRunPodApi(string text, string mediaType)
    {
        var apiKey = configuration["RunPod:ApiKey"];
        var endpointId = mediaType == "novels"
            ? configuration["RunPod:NovelsEndpointId"]
            : configuration["RunPod:ShowsEndpointId"];

        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(endpointId))
        {
            logger.LogError("RunPod configuration missing - ensure RunPod:ApiKey and endpoint IDs are set");
            return null;
        }

        var strippedText = FuriganaHintExtractor.Strip(text);

        var (thinnedText, wasThinned) = ThinTextToLimit(strippedText, MaxPayloadChars);
        if (wasThinned)
        {
            logger.LogInformation("Text thinned from {Original} to {Thinned} chars",
                strippedText.Length, thinnedText.Length);
        }

        await ApiThrottle.WaitAsync();
        try
        {
            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            var runRequest = new RunPodRequest(new RunPodInput(thinnedText));
            var runResponse = await client.PostAsJsonAsync(
                $"https://api.runpod.ai/v2/{endpointId}/run", runRequest);

            if (!runResponse.IsSuccessStatusCode)
            {
                var errorContent = await runResponse.Content.ReadAsStringAsync();
                logger.LogError("RunPod run request failed with {StatusCode}: {Error}",
                    runResponse.StatusCode, errorContent);
                return null;
            }

            var runResult = await runResponse.Content.ReadFromJsonAsync<RunPodRunResponse>();
            if (runResult == null || string.IsNullOrEmpty(runResult.Id))
            {
                logger.LogError("RunPod run response missing job ID");
                return null;
            }

            logger.LogInformation("RunPod job {JobId} started, polling for completion", runResult.Id);

            return await PollRunPodStatus(client, endpointId, runResult.Id);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError("RunPod API request timed out");
            return null;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "RunPod API connection error");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error calling RunPod API");
            return null;
        }
        finally
        {
            ApiThrottle.Release();
        }
    }

    private async Task<DifficultyResponse?> PollRunPodStatus(HttpClient client, string endpointId, string jobId)
    {
        var maxAttempts = 600; // 10 minutes at 1 second intervals
        for (var i = 0; i < maxAttempts; i++)
        {
            await Task.Delay(1000);

            var statusResponse = await client.GetAsync(
                $"https://api.runpod.ai/v2/{endpointId}/status/{jobId}");

            if (!statusResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("RunPod status check failed with {StatusCode}", statusResponse.StatusCode);
                continue;
            }

            var status = await statusResponse.Content.ReadFromJsonAsync<RunPodStatusResponse>();
            if (status == null) continue;

            switch (status.Status)
            {
                case "COMPLETED":
                    logger.LogInformation("RunPod job {JobId} completed successfully", jobId);
                    return status.Output;
                case "FAILED":
                    logger.LogError("RunPod job {JobId} failed: {Error}", jobId, status.Error ?? "No error details");
                    return null;
                case "CANCELLED":
                    logger.LogError("RunPod job {JobId} was cancelled", jobId);
                    return null;
                default:
                    if (i % 30 == 0)
                        logger.LogDebug("RunPod job {JobId} status: {Status}", jobId, status.Status);
                    break;
            }
        }

        logger.LogError("RunPod job {JobId} timed out after polling", jobId);
        return null;
    }

    private static (string text, bool wasThinned) ThinTextToLimit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return (text, false);

        var sentences = SplitIntoSentences(text);
        if (sentences.Count <= 1)
            return (text[..maxChars], true);

        var totalLength = sentences.Sum(s => s.Length);
        var removalRatio = 1.0 - ((double)maxChars / totalLength);
        var removalInterval = Math.Max(2, (int)Math.Ceiling(1.0 / removalRatio));

        var thinnedSentences = sentences
            .Where((_, index) => index % removalInterval != 0)
            .ToList();

        var thinnedText = string.Concat(thinnedSentences);

        if (thinnedText.Length > maxChars)
            return ThinTextToLimit(thinnedText, maxChars);

        return (thinnedText, true);
    }

    private static List<string> SplitIntoSentences(string text)
    {
        var sentences = new List<string>();
        var currentStart = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (SentenceEndings.Contains(text[i]))
            {
                sentences.Add(text[currentStart..(i + 1)]);
                currentStart = i + 1;
            }
        }

        if (currentStart < text.Length)
            sentences.Add(text[currentStart..]);

        return sentences;
    }

    private static string GetApiMediaType(MediaType mediaType) => mediaType switch
    {
        MediaType.Novel or MediaType.NonFiction or MediaType.VideoGame
            or MediaType.VisualNovel or MediaType.Manga or MediaType.WebNovel => "novels",
        _ => "shows"
    };
}
