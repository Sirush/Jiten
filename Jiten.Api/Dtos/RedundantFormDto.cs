using Jiten.Core.Data.FSRS;

namespace Jiten.Api.Dtos;

public class RedundantFormDto
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string Reading { get; set; } = "";
    public FsrsState State { get; set; }
    public int ReviewCount { get; set; }
    public byte CoveringReadingIndex { get; set; }
    public string CoveringReading { get; set; } = "";
    public FsrsState CoveringState { get; set; }
    public string? MainDefinition { get; set; }
    public int FrequencyRank { get; set; }
}
