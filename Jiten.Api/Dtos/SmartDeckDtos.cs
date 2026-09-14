using Jiten.Core.Data;
using Jiten.Core.Services.SmartDeck;

namespace Jiten.Api.Dtos;

public class SmartDeckTitleDto
{
    public int DeckId { get; set; }
    public string OriginalTitle { get; set; } = "";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public string? CoverName { get; set; }
    public int MediaType { get; set; }
    public DeckStatus Status { get; set; }
    public double Weight { get; set; }
    public bool Pinned { get; set; }
    public bool Boosted { get; set; }
    public bool Planning { get; set; }
    public bool ManuallyIncluded { get; set; }
    public DateTime LastActivity { get; set; }
    public int? CursorDeckId { get; set; }
    public List<SmartDeckUnitDto> Window { get; set; } = [];
    public int CompletedUnits { get; set; }
    public int TotalUnits { get; set; }
    public bool Sequential { get; set; }
    public string WindowSource { get; set; } = "none";
}

public class SmartDeckUnitDto
{
    public int DeckId { get; set; }
    public string OriginalTitle { get; set; } = "";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public int DeckOrder { get; set; }
}

public class SmartDeckStatusDto
{
    public bool Locked { get; set; }
    public bool Exists { get; set; }
    public bool IsActive { get; set; }
    public int? UserStudyDeckId { get; set; }
    public SmartDeckSettings Settings { get; set; } = new();
    public bool ExcludeKana { get; set; }
    public int? MinGlobalFrequency { get; set; }
    public int? MaxGlobalFrequency { get; set; }
    public string? PosFilter { get; set; }
    public List<SmartDeckTitleDto> Titles { get; set; } = [];
    public List<SmartDeckUnitDto> ListedTitles { get; set; } = [];
    public int TitlesBeyondCap { get; set; }
    public int WordCount { get; set; }
    public DateTime? LastRebuiltAt { get; set; }
    public bool Building { get; set; }
    public SmartDeckPreviewCountDto? Preview { get; set; }
    public int MaxPins { get; set; } = SmartDeckConstants.MaxPins;
    public int MaxTitles { get; set; } = SmartDeckConstants.MaxTitles;
}

public class SmartDeckReasonDto
{
    public int DeckId { get; set; }
    public string OriginalTitle { get; set; } = "";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public int Occurrences { get; set; }
    public int? UnitDeckId { get; set; }
    public string? UnitOriginalTitle { get; set; }
    public string? UnitRomajiTitle { get; set; }
    public string? UnitEnglishTitle { get; set; }
    public int UnitOccurrences { get; set; }
    /// <summary>This title's fraction of the word's score, 0 to 1.</summary>
    public double Share { get; set; }
}

public class SmartDeckPreviewCountDto
{
    public int Words { get; set; }
    public int NewWords { get; set; }
    public int WindowWords { get; set; }
}

public class SmartDeckSettingsRequest
{
    public bool WeighPlanning { get; set; }
    public int LookaheadUnits { get; set; } = SmartDeckConstants.DefaultLookaheadUnits;
    public int TargetPercentage { get; set; } = SmartDeckConstants.DefaultTargetPercentage;
    public int RecencyHalfLifeDays { get; set; } = SmartDeckConstants.DefaultRecencyHalfLifeDays;
    public List<int> PinnedDeckIds { get; set; } = [];
    public List<int> IncludedDeckIds { get; set; } = [];
    public List<int> ExcludedDeckIds { get; set; } = [];
    public Dictionary<int, bool> SequenceOverrides { get; set; } = new();
    public bool ExcludeKana { get; set; }
    public int? MinGlobalFrequency { get; set; }
    public int? MaxGlobalFrequency { get; set; }
    public string? PosFilter { get; set; }
}

public class SmartDeckUnitReportDto
{
    public int DeckId { get; set; }
    public string OriginalTitle { get; set; } = "";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public int? ParentDeckId { get; set; }
    public string? ParentOriginalTitle { get; set; }
    public string? ParentRomajiTitle { get; set; }
    public string? ParentEnglishTitle { get; set; }
    public int TotalWords { get; set; }
    public int TrackedWords { get; set; }
    public int LearnedLast7Days { get; set; }
    public int Learning { get; set; }
    public int Young { get; set; }
    public int Mature { get; set; }
    public int NotYetStudied { get; set; }
    public List<DictionaryEntryDto> LearnedLast7DaysExamples { get; set; } = [];
    public List<DictionaryEntryDto> LearningExamples { get; set; } = [];
    public List<DictionaryEntryDto> YoungExamples { get; set; } = [];
    public List<DictionaryEntryDto> MatureExamples { get; set; } = [];
    public DateTime ComputedAt { get; set; }
}

public class SmartDeckSourceActionRequest
{
    /// <summary>pin, unpin, include, exclude, or clear (removes the deck from every list).</summary>
    public string Action { get; set; } = "";
}
