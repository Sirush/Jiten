namespace Jiten.Api.Dtos;

public class DeckDifficultyDto
{
    public decimal Difficulty { get; set; }
    public decimal Peak { get; set; }
    public Dictionary<string, decimal> Deciles { get; set; } = new();
    public List<ProgressionSegmentDto> Progression { get; set; } = [];
    public DateTimeOffset LastUpdated { get; set; }
    public int DistinctVoterCount { get; set; }
    public decimal UserAdjustment { get; set; }
    public decimal AdjustmentConfidence { get; set; }
}

public class ProgressionSegmentDto
{
    public int Segment { get; set; }
    public decimal Difficulty { get; set; }
    public decimal Peak { get; set; }
    public int? ChildStartOrder { get; set; }
    public int? ChildEndOrder { get; set; }
}
