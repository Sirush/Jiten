using System.Text.Json.Serialization;
using Jiten.Core.Data;

namespace Jiten.Api.Services.ReadingCheck;

public sealed record ReadingUnitDto(int DeckId, string Title, bool InProgress, bool Ticked);

/// <summary>One picker row: a deck with volumes, a series of separate decks, or a single work.</summary>
public sealed record ReadingWorkDto(
    string Key,
    string OriginalTitle,
    string? RomajiTitle,
    string? EnglishTitle,
    string? CoverName,
    MediaType MediaType,
    bool FromMediaList,
    List<ReadingUnitDto> Units);

public sealed record ReadingCountDto(int Units, int Candidates);

public sealed record ReadingItemDto(int Id, string Text, string Reading, List<string> Meanings);

/// <summary>What one risk level would mark; <paramref name="Known"/> includes the words answered "Knew".</summary>
public sealed record ReadingLevelOptionDto(int Level, int Known, int ExpectedWrong);

public sealed record ReadingStepDto(
    string SessionId,
    string Phase,
    List<ReadingItemDto> Items,
    int Answered,
    int EstimatedTotal,
    List<ReadingLevelOptionDto> Levels,
    int Level,
    int Known);

public sealed record ReadingAnswerDto(int Id, ReadingCheckAnswer Answer);

public sealed record ReadingStudyWordDto(int WordId, byte ReadingIndex, string Text, string Reading, string Meaning, int Occurrences);

public sealed record ReadingResultDto(
    int Seeded,
    Guid? BatchId,
    int? RetentionEncounters,
    List<ReadingStudyWordDto> StudyList);

public enum ReadingItemKind : byte
{
    Probe = 0,
    Verify = 2
}

public enum ReadingPhase : byte
{
    Probe = 0,
    Fit = 1,
    Verify = 3
}

public sealed class ReadingCheckItem
{
    public int Id { get; set; }
    public ReadingItemKind Kind { get; set; }
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public int Occurrences { get; set; }
    public int Rank { get; set; }
    public int Works { get; set; }
    public double Offset { get; set; }
    public bool Expression { get; set; }
    public ReadingCheckAnswer? Answer { get; set; }

    [JsonIgnore]
    public ReadingCandidate Candidate => new(WordId, ReadingIndex, Occurrences, Rank, Works, Offset: Offset, Expression: Expression);
}

/// <summary>Server-side state of one reading check. Candidates are recomputed from <see cref="DeckIds"/> rather than stored.</summary>
public sealed class ReadingCheckSession : ICheckSession
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public List<int> DeckIds { get; set; } = [];
    public ReadingPhase Phase { get; set; }
    public List<ReadingCheckItem> Items { get; set; } = [];

    public int Level { get; set; } = ReadingCheckEngine.DefaultLevel;
    public int LastKnown { get; set; } = -1;

    /// <summary>Never decreases while words are asked, so the progress label doesn't count backwards.</summary>
    public int EstimatedTotal { get; set; }

    [JsonIgnore]
    public IEnumerable<ReadingCheckItem> Pending => Items.Where(i => i.Answer == null);

    [JsonIgnore]
    public int Answered => Items.Count(i => i.Answer != null && i.Kind != ReadingItemKind.Verify);

    [JsonIgnore]
    public IEnumerable<ReadingCheckItem> PendingReview => Items.Where(i => i.Kind == ReadingItemKind.Verify && i.Answer == null);

    /// <summary>Review-page answers are evidence too, so they feed the fit.</summary>
    public List<(ReadingCandidate Candidate, bool Known)> FitData()
        => Items.Where(i => i.Answer != null).Select(i => (i.Candidate, i.Answer == ReadingCheckAnswer.Knew)).ToList();
}

/// <summary>Last finished reading check, kept on the user's settings row for the Settings card.</summary>
public sealed class ReadingCheckRunState
{
    public DateTime FinishedAt { get; set; }
    public int Units { get; set; }
    public int Seeded { get; set; }
    public int? RetentionEncounters { get; set; }
    public Guid? BatchId { get; set; }
}
