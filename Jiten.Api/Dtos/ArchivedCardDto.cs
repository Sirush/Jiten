using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Dtos;

public class ArchivedCardDto
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string Reading { get; set; } = "";
    public string? MainDefinition { get; set; }
    public int FrequencyRank { get; set; }

    public DateTime ArchivedAt { get; set; }
    public CardArchiveReason Reason { get; set; }
    public byte? CoveringReadingIndex { get; set; }
    public string? CoveringReading { get; set; }

    public FsrsState State { get; set; }
    public int ReviewCount { get; set; }
    public DateTime? FirstReview { get; set; }
    public DateTime? LastReview { get; set; }
    public int Lapses { get; set; }

    /// <summary>The stored history holds fewer reviews than <see cref="ReviewCount"/>.</summary>
    public bool HistoryTruncated { get; set; }
    public bool AutoRestores { get; set; }
}
