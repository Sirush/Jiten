using Jiten.Core.Data;

namespace Jiten.Api.Dtos.Requests;

public class DeckDownloadRequest
{
    public DeckFormat Format { get; set; }
    public DeckDownloadType DownloadType { get; set; }
    public DeckOrder Order { get; set; }
    public int MinFrequency { get; set; }
    public int MaxFrequency { get; set; }
    public bool ExcludeKana { get; set; }
    public bool ExcludeMatureMasteredBlacklisted { get; set; }
    public bool ExcludeAllTrackedWords { get; set; }
    public bool ExcludeExampleSentences { get; set; }
    public float? TargetPercentage { get; set; }
    public bool StartFromKnown { get; set; }
    public int? MinOccurrences { get; set; }
    public int? MaxOccurrences { get; set; }

    /// <summary>Ranking the frequency filter and sort read from; null is the site-wide ranking.</summary>
    public MediaType? FrequencySource { get; set; }
}
