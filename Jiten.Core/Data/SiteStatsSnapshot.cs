namespace Jiten.Core.Data;

/// <summary>One public /stats snapshot per UTC day; older rows are history for growth charts.</summary>
public class SiteStatsSnapshot
{
    public DateOnly Date { get; set; }
    public DateTime ComputedAt { get; set; }
    public string Payload { get; set; } = "{}";
}
