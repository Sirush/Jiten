using System.Text.Json;
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

[JsonConverter(typeof(JsonStringEnumConverter<CardImageLayout>))]
public enum CardImageLayout
{
    [JsonStringEnumMemberName("beside")] Beside,
    [JsonStringEnumMemberName("below")] Below
}

[JsonConverter(typeof(JsonStringEnumConverter<CardImagePosition>))]
public enum CardImagePosition
{
    Back,
    Front
}

[JsonConverter(typeof(JsonStringEnumConverter<CardAudioAutoPlayPosition>))]
public enum CardAudioAutoPlayPosition
{
    Back,
    Front,
    Both
}

[JsonConverter(typeof(JsonStringEnumConverter<LeechAction>))]
public enum LeechAction
{
    Suspend,
    NotifyOnly
}

[JsonConverter(typeof(JsonStringEnumConverter<TimedRevealAction>))]
public enum TimedRevealAction
{
    Reveal,
    FailLearn,
    Nudge
}

[JsonConverter(typeof(JsonStringEnumConverter<TimedAnswerAction>))]
public enum TimedAnswerAction
{
    SoftFail,
    HardFail
}

[JsonConverter(typeof(JsonStringEnumConverter<WriteInWrongBehavior>))]
public enum WriteInWrongBehavior
{
    Reveal,
    Retry
}

[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class StudySettingsDto
{
    [JsonPropertyName("newCardsPerDay")]
    public int NewCardsPerDay { get; set; } = 20;

    [JsonPropertyName("maxReviewsPerDay")]
    public int MaxReviewsPerDay { get; set; } = 200;

    [JsonPropertyName("batchSize")]
    public int BatchSize { get; set; } = 100;

    [JsonPropertyName("pauseBetweenBatches")]
    public bool PauseBetweenBatches { get; set; } = true;

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

    [JsonPropertyName("cardImageLayout")]
    public CardImageLayout CardImageLayout { get; set; } = CardImageLayout.Beside;

    [JsonPropertyName("cardImagePosition")]
    public CardImagePosition CardImagePosition { get; set; } = CardImagePosition.Back;

    [JsonPropertyName("blurCardImage")]
    public bool BlurCardImage { get; set; } = true;

    [JsonPropertyName("showFrequencyRank")]
    public bool ShowFrequencyRank { get; set; } = true;

    [JsonPropertyName("showKanjiBreakdown")]
    public bool ShowKanjiBreakdown { get; set; } = true;

    [JsonPropertyName("showWordComposition")]
    public bool ShowWordComposition { get; set; } = true;

    [JsonPropertyName("showWordUsedIn")]
    public bool ShowWordUsedIn { get; set; } = true;

    [JsonPropertyName("showNextInterval")]
    public bool ShowNextInterval { get; set; } = true;

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

    [JsonPropertyName("autoPlayCustomAudio")]
    public bool AutoPlayCustomAudio { get; set; } = true;

    [JsonPropertyName("autoPlayCustomAudioPosition")]
    public CardAudioAutoPlayPosition AutoPlayCustomAudioPosition { get; set; } = CardAudioAutoPlayPosition.Back;

    [JsonPropertyName("customAudioReplacesHeadword")]
    public bool CustomAudioReplacesHeadword { get; set; } = true;

    [JsonPropertyName("customAudioReplacesSentence")]
    public bool CustomAudioReplacesSentence { get; set; } = true;

    /// <summary>
    /// Below <see cref="StudySettingsMigrator.CurrentAudioDefaultsVersion"/> the custom-audio fields are not
    /// authoritative and get overwritten with the current defaults on both read and write.
    /// </summary>
    [JsonPropertyName("audioDefaultsVersion")]
    public int AudioDefaultsVersion { get; set; }

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

    [JsonPropertyName("loadBalancing")]
    public bool LoadBalancing { get; set; } = true;

    /// <summary>
    /// Per-weekday load preference ("Easy Days"), 7 weights indexed by <see cref="DayOfWeek"/>
    /// (0 = Sunday … 6 = Saturday), each in [0, 1]: 1 = normal, 0.5 = reduced, 0 = avoid. Null or all-1.0
    /// means the feature is off. Only takes effect while <see cref="LoadBalancing"/> is enabled.
    /// </summary>
    [JsonPropertyName("easyDays")]
    public double[]? EasyDays { get; set; }

    /// <summary>
    /// Derivational categories (<see cref="Jiten.Core.Data.JMDict.DerivationCategories"/> keys) whose
    /// derived entries count as known once a family member is known. Empty means the feature is off; null on a
    /// PUT means unchanged, so a client that predates the field cannot clear it.
    /// </summary>
    [JsonPropertyName("derivationalRedundancyCategories")]
    public List<string>? DerivationalRedundancyCategories { get; set; }

    [JsonPropertyName("leechThreshold")]
    public int LeechThreshold { get; set; } = 8;

    [JsonPropertyName("leechAction")]
    public LeechAction LeechAction { get; set; } = LeechAction.NotifyOnly;

    /// <summary>
    /// "Speed Focus" timed-review preferences. Purely client-side behaviour — the server stores and
    /// returns it inside the settings blob but takes no action on it.
    /// </summary>
    [JsonPropertyName("timedReview")]
    public TimedReviewSettingsDto TimedReview { get; set; } = new();

    /// <summary>
    /// "Write-in review" preferences. Purely client-side behaviour — the server stores and returns it
    /// inside the settings blob but takes no action on it.
    /// </summary>
    [JsonPropertyName("writeInReview")]
    public WriteInReviewSettingsDto WriteInReview { get; set; } = new();

    [JsonPropertyName("keybinds")]
    public StudyKeybindsDto Keybinds { get; set; } = new();

    /// <summary>
    /// User-customised ordering of the SRS card blocks per side. Null means "not customised" — the
    /// client derives the layout from the legacy display toggles. The server bounds its size but does
    /// not interpret block types; the client registry owns their semantics.
    /// </summary>
    [JsonPropertyName("cardLayout")]
    public CardLayoutDto? CardLayout { get; set; }

    [JsonPropertyName("cardLayoutPresets")]
    public List<CardLayoutPresetDto>? CardLayoutPresets { get; set; }
}

[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class CardLayoutDto
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("front")] public List<CardLayoutBlockDto> Front { get; set; } = new();
    [JsonPropertyName("back")] public List<CardLayoutBlockDto> Back { get; set; } = new();
}

[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class CardLayoutBlockDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("options")] public Dictionary<string, JsonElement>? Options { get; set; }
}

[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class CardLayoutPresetDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("layout")] public CardLayoutDto Layout { get; set; } = new();
}

[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class WriteInReviewSettingsDto
{
    // Modality — which review styles are in the rotation. Defaults to standard cards only, so existing
    // users see no change. When more than one is enabled, each card draws a style (see frontend mixer).
    [JsonPropertyName("modalitySrs")] public bool ModalitySrs { get; set; } = true;
    [JsonPropertyName("modalityReading")] public bool ModalityReading { get; set; }
    [JsonPropertyName("modalityMeaning")] public bool ModalityMeaning { get; set; }

    // false = input lives in the bottom bar (default); true = inline under the word inside the card.
    [JsonPropertyName("inlineInput")] public bool InlineInput { get; set; }

    [JsonPropertyName("wrongBehavior")] public WriteInWrongBehavior WrongBehavior { get; set; } = WriteInWrongBehavior.Reveal;

    // Reading mode: convert romaji to kana as you type (wanakana IME).
    [JsonPropertyName("romajiInput")] public bool RomajiInput { get; set; } = true;

    // Meaning mode: show the reading (furigana) on the front. Off by default — hiding it tests
    // reading recognition alongside the meaning. Turn on to show the reading and test meaning only.
    [JsonPropertyName("meaningShowReading")] public bool MeaningShowReading { get; set; }

    // Brand-new cards can't be recalled — skip write-in for them and just reveal.
    [JsonPropertyName("skipNewCards")] public bool SkipNewCards { get; set; } = true;

    // Auto-advance: after the answer is graded by the check, animate the suggested grade then commit it.
    [JsonPropertyName("autoAdvance")] public bool AutoAdvance { get; set; }
    [JsonPropertyName("autoAdvanceWrong")] public bool AutoAdvanceWrong { get; set; }
    [JsonPropertyName("autoAdvanceSeconds")] public double AutoAdvanceSeconds { get; set; } = 2;

    // Subtle correct/wrong chime (Web Audio, no asset).
    [JsonPropertyName("sound")] public bool Sound { get; set; }

    // Keep timed review running on write-in cards. Off by default — the question timer would auto-reveal
    // before you finish typing. Standard cards are still timed normally regardless of this.
    [JsonPropertyName("timed")] public bool Timed { get; set; }
}

[JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
public class TimedReviewSettingsDto
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("showTimer")] public bool ShowTimer { get; set; } = true;
    [JsonPropertyName("skipNewCards")] public bool SkipNewCards { get; set; } = true;
    [JsonPropertyName("revealEnabled")] public bool RevealEnabled { get; set; } = true;
    [JsonPropertyName("revealSeconds")] public double RevealSeconds { get; set; } = 8;
    [JsonPropertyName("revealAction")] public TimedRevealAction RevealAction { get; set; } = TimedRevealAction.Reveal;
    [JsonPropertyName("answerEnabled")] public bool AnswerEnabled { get; set; } = true;
    [JsonPropertyName("answerSeconds")] public double AnswerSeconds { get; set; } = 4;
    [JsonPropertyName("answerAction")] public TimedAnswerAction AnswerAction { get; set; } = TimedAnswerAction.SoftFail;
    [JsonPropertyName("alertSound")] public bool AlertSound { get; set; } = true;
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
    [JsonPropertyName("pauseTimer")] public string PauseTimer { get; set; } = "p";
    [JsonPropertyName("dictPrev")] public string DictPrev { get; set; } = "ArrowLeft";
    [JsonPropertyName("dictNext")] public string DictNext { get; set; } = "ArrowRight";
}
