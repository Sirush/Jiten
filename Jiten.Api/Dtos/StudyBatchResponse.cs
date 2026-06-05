namespace Jiten.Api.Dtos;

public class StudyBatchResponse
{
    public string SessionId { get; set; } = "";
    public List<StudyCardDto> Cards { get; set; } = new();
    public int NewCardsRemaining { get; set; }
    public int ReviewsRemaining { get; set; }
    public int NewCardsToday { get; set; }
    public int ReviewsToday { get; set; }
}

public class StudyCardDto
{
    public long CardId { get; set; }
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public int State { get; set; }
    public bool IsNewCard { get; set; }
    public string WordText { get; set; } = "";
    public string WordTextPlain { get; set; } = "";
    public List<StudyReadingDto> Readings { get; set; } = new();
    public List<StudyDefinitionDto> Definitions { get; set; } = new();
    public string[] PartsOfSpeech { get; set; } = Array.Empty<string>();
    public int[]? PitchAccents { get; set; }
    public int FrequencyRank { get; set; }
    public StudyExampleSentenceDto? ExampleSentence { get; set; }
    public IntervalPreviewDto? IntervalPreview { get; set; }
    public List<StudyDeckOccurrenceDto>? DeckOccurrences { get; set; }
    public string? SourceDeckName { get; set; }
    public List<string>? ConfusableReadings { get; set; }
}

public class StudyDeckOccurrenceDto
{
    public int DeckId { get; set; }
    public string OriginalTitle { get; set; } = "";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public int Occurrences { get; set; }
    public string? ParentOriginalTitle { get; set; }
    public string? ParentRomajiTitle { get; set; }
    public string? ParentEnglishTitle { get; set; }
}

public class IntervalPreviewDto
{
    public int AgainSeconds { get; set; }
    public int HardSeconds { get; set; }
    public int GoodSeconds { get; set; }
    public int EasySeconds { get; set; }
}

public class StudyReadingDto
{
    public string Text { get; set; } = "";
    public string RubyText { get; set; } = "";
    public int ReadingIndex { get; set; }
    public int FormType { get; set; }
}

public class StudyDefinitionDto
{
    public int Index { get; set; }
    public string[] Meanings { get; set; } = Array.Empty<string>();
    public string[] PartsOfSpeech { get; set; } = Array.Empty<string>();
}

public class StudyExampleSentenceDto
{
    public int SentenceId { get; set; }
    public string Text { get; set; } = "";
    public int WordPosition { get; set; }
    public int WordLength { get; set; }
    public StudyExampleSourceDto? SourceDeck { get; set; }
    public StudyExampleSourceDto? SourceParent { get; set; }
    public bool IsCustom { get; set; }
    public string? CustomSource { get; set; }
    public string? CustomText { get; set; }
}

public class StudyExampleSourceDto
{
    public int DeckId { get; set; }
    public string OriginalTitle { get; set; } = "";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public int MediaType { get; set; }
}

public class CardExamplesRequest
{
    public List<WordPair> Pairs { get; set; } = new();
    public class WordPair
    {
        public int WordId { get; set; }
        public byte ReadingIndex { get; set; }
    }
}

public class CardExamplesResponse
{
    public Dictionary<string, StudyExampleSentenceDto> Examples { get; set; } = new();
}

public class StudyDeckDto
{
    public int UserStudyDeckId { get; set; }
    public StudyDeckType DeckType { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int? DeckId { get; set; }
    public string Title { get; set; } = "";
    public string? RomajiTitle { get; set; }
    public string? EnglishTitle { get; set; }
    public string? CoverName { get; set; }
    public int MediaType { get; set; }
    public int SortOrder { get; set; }
    public int DownloadType { get; set; }
    public int Order { get; set; }
    public int MinFrequency { get; set; }
    public int MaxFrequency { get; set; }
    public float? TargetPercentage { get; set; }
    public bool StartFromKnown { get; set; }
    public int? MinOccurrences { get; set; }
    public int? MaxOccurrences { get; set; }
    public bool ExcludeKana { get; set; }
    public int? MinGlobalFrequency { get; set; }
    public int? MaxGlobalFrequency { get; set; }
    public string? PosFilter { get; set; }
    public int TotalWords { get; set; }
    public int UnseenCount { get; set; }
    public int LearningCount { get; set; }
    public int ReviewCount { get; set; }
    public int YoungCount { get; set; }
    public int MatureCount { get; set; }
    public int MasteredCount { get; set; }
    public int BlacklistedCount { get; set; }
    public int SuspendedCount { get; set; }
    public int DueReviewCount { get; set; }
    public bool IsActive { get; set; }
    public string? Warning { get; set; }
    public int? ParentDeckId { get; set; }
    public string? ParentTitle { get; set; }
    public string? ParentRomajiTitle { get; set; }
    public string? ParentEnglishTitle { get; set; }
    public string? ParentCoverName { get; set; }
}

