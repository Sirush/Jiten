using Hangfire;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Jiten.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Jobs;

/// <summary>Generates a roadmap off-request: the known set changes after every step, so candidate word lists are held fully in memory (~100 MB at the cap).</summary>
public class RoadmapJob(
    IDbContextFactory<UserDbContext> userContextFactory,
    IRoadmapDataLoader loader,
    ILogger<RoadmapJob> logger)
{
    /// <summary>Upper bound on candidate decks pulled into memory for one run.</summary>
    public const int MaxCandidates = 1500;

    /// <summary>Cap on per-step word keys in the payload; bands and totals use the full acquisition set, so counts stay honest.</summary>
    private const int WordsPerStep = 500;

    /// <summary>How many drill words are surfaced when nothing clears the floor.</summary>
    private const int DrillWordsShown = 100;

    // In-process gates, like the controller's rate limiter: both assume a single API/Hangfire instance.
    private static readonly object GateLock = new();
    private static readonly HashSet<long> Generating = new();

    /// <summary>A run costs ~100 MB; the default Hangfire worker count is more than that can afford.</summary>
    private static readonly SemaphoreSlim ConcurrencyGate = new(2);

    [Queue("default")]
    [AutomaticRetry(Attempts = 1)]
    public async Task Generate(long roadmapId)
    {
        lock (GateLock)
        {
            if (!Generating.Add(roadmapId))
            {
                logger.LogInformation("RoadmapJob: {RoadmapId} is already generating, skipping", roadmapId);
                return;
            }
        }

        try
        {
            await ConcurrencyGate.WaitAsync();
            try
            {
                await GenerateInternal(roadmapId);
            }
            finally
            {
                ConcurrencyGate.Release();
            }
        }
        finally
        {
            lock (GateLock)
                Generating.Remove(roadmapId);
        }
    }

    private async Task GenerateInternal(long roadmapId)
    {
        await using var userContext = await userContextFactory.CreateDbContextAsync();

        var roadmap = await userContext.UserRoadmaps.FirstOrDefaultAsync(r => r.Id == roadmapId);
        if (roadmap is null)
        {
            logger.LogWarning("RoadmapJob: roadmap {RoadmapId} no longer exists", roadmapId);
            return;
        }

        roadmap.Status = RoadmapStatus.Generating;
        roadmap.FailureReason = null;
        await userContext.SaveChangesAsync();

        try
        {
            var definition = roadmap.Definition;

            var known = await loader.LoadKnownWordsAsync(roadmap.UserId, definition.IncludeLearningWords);
            var set = await loader.LoadCandidatesAsync(roadmap.UserId, definition, roadmap.GoalDeckId, MaxCandidates);

            if (set.Candidates.Count == 0)
            {
                await FailAsync(userContext, roadmap,
                                "No media matched those filters. Try widening the media types, genres or difficulty band.");
                return;
            }

            if (roadmap.Mode == RoadmapMode.Goal && set.Goal is null)
            {
                await FailAsync(userContext, roadmap,
                                "That goal deck has no vocabulary data yet, so a route to it can't be computed.");
                return;
            }

            // Rank lookup covers every word that could plausibly be scored, loaded once.
            var scoreableKeys = new HashSet<long>();
            foreach (var candidate in set.Candidates)
            {
                foreach (var word in candidate.Words)
                {
                    if (word.Occurrences < definition.AcquisitionThreshold)
                        break;
                    scoreableKeys.Add(word.Key);
                }
            }

            var ranks = await loader.LoadFrequencyRanksAsync(scoreableKeys);

            var result = RoadmapEngine.Build(new RoadmapEngine.RoadmapInput
            {
                Settings = definition,
                Candidates = set.Candidates,
                KnownWords = known,
                FrequencyRanks = ranks,
                Prerequisites = set.Prerequisites,
                CompletedDeckIds = set.CompletedDeckIds,
                SeedVectors = set.SeedVectors,
                Goal = set.Goal
            });

            // The drill's gap walk includes below-threshold words the prefetch skipped; top up their ranks.
            if (result.Drill is not null)
            {
                var missing = result.Drill.Words.Where(w => !ranks.ContainsKey(w.Key)).Select(w => w.Key).ToList();
                if (missing.Count > 0)
                {
                    foreach (var (key, rank) in await loader.LoadFrequencyRanksAsync(missing))
                        ranks[key] = rank;
                }
            }

            var payload = await BuildPayloadAsync(result, set, ranks);

            roadmap.Payload = payload;
            roadmap.StepCount = payload.Steps.Count;
            roadmap.CandidateCount = set.Candidates.Count;
            roadmap.Status = RoadmapStatus.Ready;
            roadmap.GeneratedAt = DateTime.UtcNow;
            await userContext.SaveChangesAsync();

            logger.LogInformation("RoadmapJob: roadmap {RoadmapId} generated {Steps} steps from {Candidates} candidates",
                                  roadmapId, payload.Steps.Count, set.Candidates.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RoadmapJob: roadmap {RoadmapId} failed", roadmapId);
            await FailAsync(userContext, roadmap, "Something went wrong while building this plan. Try again.");
        }
    }

    private async Task<RoadmapPayload> BuildPayloadAsync(RoadmapEngineResult result, RoadmapCandidateSet set,
                                                         IReadOnlyDictionary<long, int> ranks)
    {
        var payload = new RoadmapPayload
        {
            GoalReached = result.GoalReached,
            GoalCeilingReached = result.GoalCeilingReached,
            GoalUnreachableWords = result.GoalUnreachableWords,
            GoalCoverageFinal = result.GoalCoverageFinal,
            GoalWordsRemaining = result.GoalWordsRemaining
        };

        // Steps can't share acquisition words by construction; dedupe defensively anyway.
        var planWords = new HashSet<long>();

        // One up-front frequency sort feeds bands, totals and the capped list alike.
        var stepWordsSorted = result.Steps.ToDictionary(
            s => s.Index,
            s => s.AcquiredWords.OrderBy(w => RankOrUnranked(w.Key, ranks)).ToList());

        foreach (var step in result.Steps)
        {
            foreach (var word in stepWordsSorted[step.Index])
                planWords.Add(word.Key);
        }

        // Only drill words resolve to text now; step words stay keys, resolved lazily on expand.
        var drillKeys = new HashSet<long>();
        if (result.Drill is not null)
        {
            foreach (var word in result.Drill.Words.Take(DrillWordsShown))
                drillKeys.Add(word.Key);
        }

        var texts = await loader.LoadWordTextsAsync(drillKeys);

        foreach (var step in result.Steps)
        {
            set.Summaries.TryGetValue(step.DeckId, out var summary);
            var words = stepWordsSorted[step.Index];

            payload.Steps.Add(new RoadmapStepDto
            {
                Index = step.Index,
                DeckId = step.DeckId,
                Title = summary?.Title ?? string.Empty,
                RomajiTitle = summary?.RomajiTitle,
                EnglishTitle = summary?.EnglishTitle,
                CoverName = summary?.CoverName,
                MediaType = summary?.MediaType ?? 0,
                Genres = summary?.Genres ?? new List<int>(),
                Difficulty = Math.Round(summary?.Difficulty ?? 0, 2),
                Coverage = Math.Round(step.Coverage, 4),
                NewWords = words.Count,
                WordCount = (int)(summary?.WordCount ?? 0),
                CharacterCount = summary?.CharacterCount ?? 0,
                SpeechDuration = summary?.SpeechDuration ?? 0,
                FrequencyBands = BuildBands(words, ranks),
                GoalCoverageAfter = step.GoalCoverageAfter.HasValue ? Math.Round(step.GoalCoverageAfter.Value, 4) : null,
                Words = words.Take(WordsPerStep).Select(w => w.Key).ToList()
            });
        }

        payload.TotalNewWords = planWords.Count;

        if (set.Goal is not null)
        {
            set.Summaries.TryGetValue(set.Goal.DeckId, out var goalSummary);
            payload.Goal = new RoadmapGoalDto
            {
                DeckId = set.Goal.DeckId,
                Title = goalSummary?.Title ?? string.Empty,
                RomajiTitle = goalSummary?.RomajiTitle,
                EnglishTitle = goalSummary?.EnglishTitle,
                CoverName = goalSummary?.CoverName,
                MediaType = goalSummary?.MediaType ?? 0,
                Difficulty = Math.Round(goalSummary?.Difficulty ?? 0, 2),
                WordCount = (int)(goalSummary?.WordCount ?? 0),
                Coverage = Math.Round(result.GoalCoverageFinal ?? 0, 4),
                Reached = result.GoalReached,
                WordsRemaining = result.GoalWordsRemaining ?? 0
            };
        }

        if (result.Drill is not null)
        {
            set.Summaries.TryGetValue(result.Drill.DeckId, out var drillSummary);

            payload.Drill = new RoadmapDrillDto
            {
                DeckId = result.Drill.DeckId,
                Title = drillSummary?.Title ?? string.Empty,
                Coverage = Math.Round(result.Drill.Coverage, 4),
                WordsNeeded = result.Drill.Words.Count,
                Words = result.Drill.Words.Take(DrillWordsShown).Select(w => ToWordDto(w, texts, ranks)).ToList()
            };
        }

        return payload;
    }

    private static int RankOrUnranked(long key, IReadOnlyDictionary<long, int> ranks)
    {
        var rank = ranks.GetValueOrDefault(key, 0);
        return rank > 0 ? rank : int.MaxValue;
    }

    private static RoadmapFrequencyBands BuildBands(IEnumerable<RoadmapWord> words, IReadOnlyDictionary<long, int> ranks)
    {
        var bands = new RoadmapFrequencyBands();
        foreach (var word in words)
        {
            var rank = ranks.GetValueOrDefault(word.Key, 0);
            if (rank <= 0) bands.Unranked++;
            else if (rank <= 3000) bands.Band0To3k++;
            else if (rank <= 10000) bands.Band3kTo10k++;
            else if (rank <= 25000) bands.Band10kTo25k++;
            else if (rank <= 50000) bands.Band25kTo50k++;
            else if (rank <= 80000) bands.Band50kTo80k++;
            else bands.Band80kPlus++;
        }

        return bands;
    }

    private static RoadmapWordDto ToWordDto(RoadmapWord word,
                                            IReadOnlyDictionary<long, (string Text, string Reading)> texts,
                                            IReadOnlyDictionary<long, int> ranks)
    {
        texts.TryGetValue(word.Key, out var text);
        return new RoadmapWordDto
        {
            WordId = RoadmapEngine.UnpackWordId(word.Key),
            ReadingIndex = RoadmapEngine.UnpackReadingIndex(word.Key),
            Text = text.Text ?? string.Empty,
            Reading = text.Reading ?? string.Empty,
            Occurrences = word.Occurrences,
            FrequencyRank = ranks.GetValueOrDefault(word.Key, 0)
        };
    }

    private static async Task FailAsync(UserDbContext context, UserRoadmap roadmap, string reason)
    {
        roadmap.Status = RoadmapStatus.Failed;
        roadmap.FailureReason = reason;
        await context.SaveChangesAsync();
    }
}
