namespace Jiten.Core.Data;

public enum DeckStatus
{
    None = 0,
    Planning = 1,
    Ongoing = 2,
    Completed = 3,
    Dropped = 4,
    Paused = 5
}

public static class DeckStatusExtensions
{
    /// <summary>A pass is under way, whether the user is reading it now or has paused it.</summary>
    public static bool IsInProgress(this DeckStatus status) => status is DeckStatus.Ongoing or DeckStatus.Paused;

    /// <summary>Which status wins when one title gets several, from merged import lists or from a series' volumes.</summary>
    public static int Rank(this DeckStatus status) => status switch
    {
        DeckStatus.Completed => 5,
        DeckStatus.Ongoing => 4,
        DeckStatus.Paused => 3,
        DeckStatus.Planning => 2,
        DeckStatus.Dropped => 1,
        _ => 0
    };
}
