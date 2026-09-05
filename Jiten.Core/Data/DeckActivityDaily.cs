namespace Jiten.Core.Data;

/// <summary>Per-deck anonymous daily counters</summary>
public class DeckActivityDaily
{
    public int DeckId { get; set; }
    public DateOnly Date { get; set; }
    public int Views { get; set; }
    public int GuestDownloads { get; set; }
}
