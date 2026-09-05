namespace Jiten.Core.Data;

/// <summary>One row per user and deck, used for popularity</summary>
public class DeckDownload
{
    public string UserId { get; set; } = null!;
    public int DeckId { get; set; }
    public DateTime FirstDownloadAt { get; set; }
}
