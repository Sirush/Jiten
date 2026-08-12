namespace Jiten.Api.Dtos;

public class KnownWordAmountDto
{
    public int Young { get; set; }
    public int YoungForm { get; set; }
    public int Mature { get; set; }
    public int MatureForm { get; set; }
    public int Mastered { get; set; }
    public int MasteredForm { get; set; }
    public int Blacklisted { get; set; }
    public int BlacklistedForm { get; set; }
    public int WordSetMastered { get; set; }
    public int WordSetMasteredForm { get; set; }
    public int WordSetBlacklisted { get; set; }
    public int WordSetBlacklistedForm { get; set; }
    public int RedundantForms { get; set; }
    /// <summary>Words known only via derivational redundancy: no card, no set membership on any form.</summary>
    public int DerivationCovered { get; set; }
    public int DerivationCoveredForm { get; set; }
    public int ArchivedCards { get; set; }
}