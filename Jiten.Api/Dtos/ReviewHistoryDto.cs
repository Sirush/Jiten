namespace Jiten.Api.Dtos;

public class ReviewHistoryDto
{
    public CardStateDto? Card { get; set; }
    public List<ReviewLogDto> Reviews { get; set; } = [];
}

public class CardStateDto
{
    public int State { get; set; }
    public double? Stability { get; set; }
    public double? Difficulty { get; set; }
    public DateTime Due { get; set; }
    public DateTime? LastReview { get; set; }
    public DateTime CreatedAt { get; set; }
    public int Lapses { get; set; }
}

public class ReviewLogDto
{
    public int Rating { get; set; }
    public DateTime ReviewDateTime { get; set; }
    public int? ReviewDuration { get; set; }
}

public class RecentReviewDto
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string WordText { get; set; } = "";
    public int Rating { get; set; }
    public DateTime ReviewDateTime { get; set; }
    public int? ReviewDuration { get; set; }
    public int CardState { get; set; }
}
