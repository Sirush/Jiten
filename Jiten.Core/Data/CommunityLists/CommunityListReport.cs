namespace Jiten.Core.Data.CommunityLists;

public class CommunityListReport
{
    public const int MaxReasonLength = 500;

    public int ReportId { get; set; }
    public int ListId { get; set; }
    public string ReporterId { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
}
