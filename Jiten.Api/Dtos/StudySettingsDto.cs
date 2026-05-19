using System.Text.Json.Serialization;

namespace Jiten.Api.Dtos;

[JsonConverter(typeof(JsonStringEnumConverter<StudyInterleaving>))]
public enum StudyInterleaving
{
    Mixed,
    NewFirst,
    ReviewsFirst
}

[JsonConverter(typeof(JsonStringEnumConverter<StudyReviewFrom>))]
public enum StudyReviewFrom
{
    AllTracked,
    StudyDecksOnly
}

[JsonConverter(typeof(JsonStringEnumConverter<StudyNewCardGathering>))]
public enum StudyNewCardGathering
{
    TopDeck,
    RoundRobin,
    CrossDeckFrequency
}

[JsonConverter(typeof(JsonStringEnumConverter<ExampleSentencePosition>))]
public enum ExampleSentencePosition
{
    Hidden,
    Back,
    Front
}

[JsonConverter(typeof(JsonStringEnumConverter<ExampleSentenceSorting>))]
public enum ExampleSentenceSorting
{
    Random,
    EasiestFirst,
    HardestFirst
}

[JsonConverter(typeof(JsonStringEnumConverter<LeechAction>))]
public enum LeechAction
{
    Suspend,
    NotifyOnly
}

[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class StudySettingsDto
{
    [JsonPropertyName("newCardsPerDay")]
    public int NewCardsPerDay { get; set; } = 20;

    [JsonPropertyName("maxReviewsPerDay")]
    public int MaxReviewsPerDay { get; set; } = 1000;

    [JsonPropertyName("batchSize")]
    public int BatchSize { get; set; } = 100;

    [JsonPropertyName("gradingButtons")]
    public int GradingButtons { get; set; } = 4;

    [JsonPropertyName("interleaving")]
    public StudyInterleaving Interleaving { get; set; } = StudyInterleaving.Mixed;

    [JsonPropertyName("newCardGathering")]
    public StudyNewCardGathering NewCardGathering { get; set; } = StudyNewCardGathering.TopDeck;

    [JsonPropertyName("reviewFrom")]
    public StudyReviewFrom ReviewFrom { get; set; } = StudyReviewFrom.AllTracked;

    [JsonPropertyName("showPitchAccent")]
    public bool ShowPitchAccent { get; set; } = true;

    [JsonPropertyName("exampleSentencePosition")]
    public ExampleSentencePosition ExampleSentencePosition { get; set; } = ExampleSentencePosition.Back;

    [JsonPropertyName("blurExampleSentence")]
    public bool BlurExampleSentence { get; set; }

    [JsonPropertyName("exampleSentenceSorting")]
    public ExampleSentenceSorting ExampleSentenceSorting { get; set; } = ExampleSentenceSorting.Random;

    [JsonPropertyName("showFrequencyRank")]
    public bool ShowFrequencyRank { get; set; } = true;

    [JsonPropertyName("showKanjiBreakdown")]
    public bool ShowKanjiBreakdown { get; set; } = true;

    [JsonPropertyName("showWordComposition")]
    public bool ShowWordComposition { get; set; } = true;

    [JsonPropertyName("showWordUsedIn")]
    public bool ShowWordUsedIn { get; set; } = true;

    [JsonPropertyName("showNextInterval")]
    public bool ShowNextInterval { get; set; }

    [JsonPropertyName("showKeybinds")]
    public bool ShowKeybinds { get; set; } = true;

    [JsonPropertyName("showElapsedTime")]
    public bool ShowElapsedTime { get; set; } = true;

    [JsonPropertyName("enableSwipeGesture")]
    public bool EnableSwipeGesture { get; set; } = true;

    [JsonPropertyName("countFailedReviews")]
    public bool CountFailedReviews { get; set; } = true;

    [JsonPropertyName("showCardStatus")]
    public bool ShowCardStatus { get; set; } = true;

    [JsonPropertyName("showFuriganaOnFront")]
    public bool ShowFuriganaOnFront { get; set; }

    [JsonPropertyName("furiganaOnFrontNewOnly")]
    public bool FuriganaOnFrontNewOnly { get; set; }

    [JsonPropertyName("autoPlayWord")]
    public bool AutoPlayWord { get; set; } = true;

    [JsonPropertyName("autoPlaySentence")]
    public bool AutoPlaySentence { get; set; } = true;

    [JsonPropertyName("autoPlayWordOnFront")]
    public bool AutoPlayWordOnFront { get; set; }

    [JsonPropertyName("autoPlayWordOnFrontNewOnly")]
    public bool AutoPlayWordOnFrontNewOnly { get; set; }

    [JsonPropertyName("autoPlaySentenceOnFront")]
    public bool AutoPlaySentenceOnFront { get; set; }

    [JsonPropertyName("showReviewActivity")]
    public bool ShowReviewActivity { get; set; } = true;

    [JsonPropertyName("showReviewForecast")]
    public bool ShowReviewForecast { get; set; } = true;

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("showConfusableReadings")]
    public bool ShowConfusableReadings { get; set; } = true;

    [JsonPropertyName("dayBoundaryScheduling")]
    public bool DayBoundaryScheduling { get; set; }

    [JsonPropertyName("leechThreshold")]
    public int LeechThreshold { get; set; } = 8;

    [JsonPropertyName("leechAction")]
    public LeechAction LeechAction { get; set; } = LeechAction.NotifyOnly;

    [JsonPropertyName("keybinds")]
    public StudyKeybindsDto Keybinds { get; set; } = new();
}

[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class StudyKeybindsDto
{
    [JsonPropertyName("grade1")] public string Grade1 { get; set; } = "1";
    [JsonPropertyName("grade2")] public string Grade2 { get; set; } = "2";
    [JsonPropertyName("grade3")] public string Grade3 { get; set; } = "3";
    [JsonPropertyName("grade4")] public string Grade4 { get; set; } = "4";
    [JsonPropertyName("flipCard")] public string FlipCard { get; set; } = " ";
    [JsonPropertyName("blacklist")] public string Blacklist { get; set; } = "b";
    [JsonPropertyName("forget")] public string Forget { get; set; } = "f";
    [JsonPropertyName("master")] public string Master { get; set; } = "m";
    [JsonPropertyName("suspend")] public string Suspend { get; set; } = "s";
    [JsonPropertyName("bury")] public string Bury { get; set; } = "h";
    [JsonPropertyName("undo")] public string Undo { get; set; } = "z";
    [JsonPropertyName("wrapUp")] public string WrapUp { get; set; } = "w";
}
