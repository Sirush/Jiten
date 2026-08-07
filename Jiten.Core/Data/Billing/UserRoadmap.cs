using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Jiten.Core.Data.Billing;

public enum RoadmapMode
{
    Discovery = 0,
    Goal = 1
}

public enum RoadmapCandidateMode
{
    Seeded = 0,
    CatalogWide = 1
}

/// <summary>Tie-break above the floor: Efficiency divides the acquisition score by deck length, Volume doesn't.</summary>
public enum RoadmapPreference
{
    Efficiency = 0,
    Volume = 1
}

public enum RoadmapStatus
{
    Pending = 0,
    Generating = 1,
    Ready = 2,
    Failed = 3
}

/// <summary>A generated "what to read next" route, materialised at generation time so re-reading it never reflects a known-word set that has moved on.</summary>
public class UserRoadmap
{
    public long Id { get; set; }

    public string UserId { get; set; } = default!;

    public string Name { get; set; } = string.Empty;

    public RoadmapMode Mode { get; set; }

    /// <summary>Target deck in <see cref="RoadmapMode.Goal"/>; null in discovery mode.</summary>
    public int? GoalDeckId { get; set; }

    /// <summary>Serialised <see cref="RoadmapDefinition"/> — the settings the run was generated with.</summary>
    public string DefinitionJson { get; set; } = "{}";

    /// <summary>Serialised <see cref="RoadmapPayload"/> — the materialised steps.</summary>
    public string StepsJson { get; set; } = "{}";

    public RoadmapStatus Status { get; set; } = RoadmapStatus.Pending;

    /// <summary>User-facing reason when <see cref="Status"/> is <see cref="RoadmapStatus.Failed"/>.</summary>
    public string? FailureReason { get; set; }

    public int CandidateCount { get; set; }
    public int StepCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? GeneratedAt { get; set; }

    [NotMapped]
    public RoadmapDefinition Definition
    {
        get
        {
            try { return JsonSerializer.Deserialize<RoadmapDefinition>(DefinitionJson) ?? new(); }
            catch (JsonException) { return new(); }
        }
        set => DefinitionJson = JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public RoadmapPayload Payload
    {
        get
        {
            try { return JsonSerializer.Deserialize<RoadmapPayload>(StepsJson) ?? new(); }
            catch (JsonException) { return new(); }
        }
        set => StepsJson = JsonSerializer.Serialize(value);
    }
}

/// <summary>The settings a run was generated with. Difficulty bounds are per model family — see <see cref="DifficultyFamily"/>.</summary>
public class RoadmapDefinition
{
    public List<int> MediaTypes { get; set; } = new();
    public List<int> GenresInclude { get; set; } = new();
    public List<int> GenresExclude { get; set; } = new();
    public List<int> TagsInclude { get; set; } = new();
    public List<int> TagsExclude { get; set; } = new();
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }

    public double? ShowsDifficultyMin { get; set; }
    public double? ShowsDifficultyMax { get; set; }
    public double? NovelsDifficultyMin { get; set; }
    public double? NovelsDifficultyMax { get; set; }

    /// <summary>Hard minimum: below this share of a deck's running text, a title is never suggested.</summary>
    public double ComprehensionFloor { get; set; } = 0.80;

    /// <summary>Soft preference above the floor; a pure hard floor makes every pick land exactly on it, where reading is least comfortable.</summary>
    public double ComfortTarget { get; set; } = 0.90;

    /// <summary>Goal mode: coverage of the goal title before it counts as reached; independent of the stepping-stone floor.</summary>
    public double GoalComprehensionTarget { get; set; } = 0.95;

    /// <summary>Occurrences of a word within one deck before it counts as acquired by reading it.</summary>
    public int AcquisitionThreshold { get; set; } = 5;

    /// <summary>Count young cards as known; true matches the site's "Total coverage" figure, false plain "Coverage".</summary>
    public bool IncludeLearningWords { get; set; } = true;

    /// <summary>Bounds on <see cref="Steps"/>, shared by the request clamp and the engine's clamp so the two cannot drift.</summary>
    public const int MinSteps = 1;
    public const int MaxSteps = 15;

    /// <summary>Safety ceiling on goal mode's walk past <see cref="Steps"/>; keeps run time and payload size finite.</summary>
    public const int MaxGoalSteps = 30;

    public int Steps { get; set; } = 5;

    /// <summary>
    /// Goal mode's own budget, kept separate from <see cref="Steps"/> so plans stored before it existed
    /// deserialise to the old ceiling rather than to the discovery default.
    /// </summary>
    public int GoalSteps { get; set; } = MaxGoalSteps;

    public RoadmapPreference Preference { get; set; } = RoadmapPreference.Efficiency;

    public RoadmapCandidateMode CandidateMode { get; set; } = RoadmapCandidateMode.Seeded;

    /// <summary>Positive favours content similar to what the user has consumed, negative favours branching out, 0 indifferent.</summary>
    public double ContentSimilarity { get; set; }

    public bool IncludeAdultOnly { get; set; }

    /// <summary>Restrict to decks carrying the AdultOnly genre (ignored unless <see cref="IncludeAdultOnly"/>).</summary>
    public bool AdultOnlyExclusive { get; set; }

    /// <summary>Swapped-away decks, never re-offered, so repeated rejection walks down the ranking.</summary>
    public List<int> ExcludedDeckIds { get; set; } = new();

    /// <summary>Accepted steps, replayed in order before the search resumes, so a swap can't reshuffle earlier steps.</summary>
    public List<int> PinnedDeckIds { get; set; } = new();
}

/// <summary>Which adapted difficulty model scored a deck. Both emit the same 0-5 scale.</summary>
public enum DifficultyFamily
{
    Shows = 0,
    Novels = 1
}

public class RoadmapPayload
{
    public List<RoadmapStepDto> Steps { get; set; } = new();

    /// <summary>When nothing clears the floor: the words to drill before the nearest deck opens up.</summary>
    public RoadmapDrillDto? Drill { get; set; }

    public bool GoalReached { get; set; }

    /// <summary>Goal mode: topped out below target because the remaining words appear only in the goal itself.</summary>
    public bool GoalCeilingReached { get; set; }

    /// <summary>Goal words reachable only by reading the goal itself (relevant when <see cref="GoalCeilingReached"/>).</summary>
    public int GoalUnreachableWords { get; set; }

    /// <summary>Projected coverage of the goal deck after the final step, in goal mode.</summary>
    public double? GoalCoverageFinal { get; set; }

    /// <summary>Words still missing from the goal deck after the final step, in goal mode.</summary>
    public int? GoalWordsRemaining { get; set; }

    /// <summary>
    /// Goal mode: the fewest words that would reach the target from the user's known set as it stood when the
    /// plan was generated. <see cref="TotalNewWords"/> is always larger — real titles teach vocabulary the
    /// goal never uses — and the two together say what the detour costs.
    /// </summary>
    public int? GoalWordsAtStart { get; set; }

    /// <summary>New words learned across the whole plan, de-duplicated (a word taught by two steps counts once).</summary>
    public int TotalNewWords { get; set; }

    /// <summary>Goal mode: the subset of <see cref="TotalNewWords"/> that the goal title actually uses.</summary>
    public int? TotalGoalNewWords { get; set; }

    /// <summary>The goal title in goal mode, rendered as the destination rather than a step; null in discovery.</summary>
    public RoadmapGoalDto? Goal { get; set; }
}

public class RoadmapGoalDto
{
    public int DeckId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public string? CoverName { get; set; }
    public int MediaType { get; set; }
    public double Difficulty { get; set; }
    public int WordCount { get; set; }

    /// <summary>Projected coverage of the goal after the last step, 0-1.</summary>
    public double Coverage { get; set; }

    /// <summary>Whether the plan reaches the goal's comprehension target.</summary>
    public bool Reached { get; set; }

    /// <summary>Words still missing from the goal after the last step (0 when reached).</summary>
    public int WordsRemaining { get; set; }
}

public class RoadmapStepDto
{
    public int Index { get; set; }
    public int DeckId { get; set; }
    public string Title { get; set; } = string.Empty;

    /// <summary>Romaji/English titles so the client can apply the user's title-language preference.</summary>
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }

    public string? CoverName { get; set; }
    public int MediaType { get; set; }
    public List<int> Genres { get; set; } = new();
    public double Difficulty { get; set; }

    /// <summary>Coverage of this deck at the moment the step is reached, 0-1.</summary>
    public double Coverage { get; set; }

    /// <summary>Words this step is projected to teach (occurrences ≥ acquisition threshold).</summary>
    public int NewWords { get; set; }

    /// <summary>Goal mode: the subset of <see cref="NewWords"/> that the goal title actually uses.</summary>
    public int? GoalNewWords { get; set; }

    public int WordCount { get; set; }

    public int CharacterCount { get; set; }

    /// <summary>Milliseconds of speech, for audio-visual titles; 0 when the deck has no timed audio.</summary>
    public long SpeechDuration { get; set; }

    /// <summary>Distribution of the new words across the <see cref="RoadmapFrequencyBands"/> rank bands.</summary>
    public RoadmapFrequencyBands FrequencyBands { get; set; } = new();

    /// <summary>Frequency-sorted packed (WordId, ReadingIndex) keys; display data is resolved on demand via the words endpoint.</summary>
    public List<long> Words { get; set; } = new();

    /// <summary>Projected coverage of the goal deck after this step, in goal mode.</summary>
    public double? GoalCoverageAfter { get; set; }
}

/// <summary>New-word counts per global-frequency band. Property names, <c>RoadmapJob.BuildBands</c> and the labels in <c>roadmap/index.vue</c> encode the same edges — retune together.</summary>
public class RoadmapFrequencyBands
{
    public int Band0To3k { get; set; }
    public int Band3kTo10k { get; set; }
    public int Band10kTo25k { get; set; }
    public int Band25kTo50k { get; set; }
    public int Band50kTo80k { get; set; }
    public int Band80kPlus { get; set; }

    /// <summary>Words with no frequency data at all (proper nouns, very rare terms).</summary>
    public int Unranked { get; set; }
}

public class RoadmapDrillDto
{
    public int DeckId { get; set; }
    public string Title { get; set; } = string.Empty;
    public double Coverage { get; set; }

    /// <summary>How many unknown words must be learned before <see cref="DeckId"/> clears the floor.</summary>
    public int WordsNeeded { get; set; }

    public List<RoadmapWordDto> Words { get; set; } = new();
}

public class RoadmapWordDto
{
    public int WordId { get; set; }
    public int ReadingIndex { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Reading { get; set; } = string.Empty;
    public int Occurrences { get; set; }

    /// <summary>Global frequency rank, 0 when the word is unranked.</summary>
    public int FrequencyRank { get; set; }
}
