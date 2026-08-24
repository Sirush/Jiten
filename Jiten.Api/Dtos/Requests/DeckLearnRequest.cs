using Jiten.Core.Data;

namespace Jiten.Api.Dtos.Requests;

public class DeckLearnRequest
{
    public DeckDownloadType DownloadType { get; set; }
    public DeckOrder Order { get; set; }
    public int MinFrequency { get; set; }
    public int MaxFrequency { get; set; }
    public bool ExcludeKana { get; set; }
    public bool ExcludeMatureMasteredBlacklisted { get; set; }
    public bool ExcludeAllTrackedWords { get; set; }
    public float? TargetPercentage { get; set; }
    public bool StartFromKnown { get; set; }
    public int? MinOccurrences { get; set; }
    public int? MaxOccurrences { get; set; }
    public MediaType? FrequencySource { get; set; }
    public string VocabularyState { get; set; } = "mastered";

    /// <summary>Space the date learned of the words so they appear on the charts.</summary>
    public bool CountAsNewlyLearned { get; set; }

    /// <summary>
    /// Projects the shared word-selection filters onto a <see cref="DeckDownloadRequest"/>
    /// (everything except the file-format options, which learn doesn't use).
    /// </summary>
    public DeckDownloadRequest ToDownloadRequest() => new()
    {
        DownloadType = DownloadType, Order = Order,
        MinFrequency = MinFrequency, MaxFrequency = MaxFrequency,
        ExcludeKana = ExcludeKana,
        ExcludeMatureMasteredBlacklisted = ExcludeMatureMasteredBlacklisted,
        ExcludeAllTrackedWords = ExcludeAllTrackedWords,
        TargetPercentage = TargetPercentage, StartFromKnown = StartFromKnown,
        MinOccurrences = MinOccurrences, MaxOccurrences = MaxOccurrences,
        FrequencySource = FrequencySource
    };
}
