namespace Jiten.Core.Data.CommunityLists;

/// <summary>Anonymous daily view counter; never exposed on public surfaces.</summary>
public class CommunityListActivityDaily
{
    public int ListId { get; set; }
    public DateOnly Date { get; set; }
    public int Views { get; set; }
}
