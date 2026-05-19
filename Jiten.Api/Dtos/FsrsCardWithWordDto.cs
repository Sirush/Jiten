using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;

namespace Jiten.Api.Dtos;

public class FsrsCardWithWordDto
{
    public long CardId { get; set; }
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public FsrsState State { get; set; }
    public int? Step { get; set; }
    public double? Stability { get; set; }
    public double? Difficulty { get; set; }
    public DateTime Due { get; set; }
    public DateTime? LastReview { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Lapses { get; set; }

    // Word data
    public string WordText { get; set; } = string.Empty;
    public JmDictReadingType ReadingType { get; set; }
    public int FrequencyRank { get; set; }
}
