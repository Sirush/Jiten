namespace Jiten.Core.Data;

public class UserDeckPreference
{
    public string UserId { get; set; } = null!;
    public int DeckId { get; set; }
    public DeckStatus Status { get; set; }
    public bool IsFavourite { get; set; }
    public bool IsIgnored { get; set; }

    /// <summary>10 to 100, shown divided by ten (8.5). Stored on the deck the title displays under, never on a child volume.</summary>
    public short? Score { get; set; }

    public const int ScoreScale = 10;
    public const short MinScore = 10;
    public const short MaxScore = 100;
    public const int MaxNoteLength = 500;

    public static decimal? ToDisplayScore(short? stored) => stored / (decimal)ScoreScale;
    public static short ToStoredScore(decimal display) => (short)Math.Round(display * ScoreScale);

    public static string? NormaliseNote(string? raw)
    {
        var note = raw?.Trim();
        return string.IsNullOrEmpty(note) ? null : note.Length > MaxNoteLength ? note[..MaxNoteLength] : note;
    }

    /// <summary>Owner-only; never serialised for another user.</summary>
    public string? Note { get; set; }

    /// <summary>Hides the title from everything another user sees on the profile; lifetime totals still count it.</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Holds something other than a status, so the row outlives the title leaving the list.</summary>
    public bool HoldsUserData => IsFavourite || IsIgnored || Score != null || Note != null || IsPrivate;

    public DateTime UpdatedAt { get; set; }

    /// <summary>The entry that status changes act on; past entries added by hand never take this over.</summary>
    public long? CurrentEntryId { get; set; }
    public UserMediaListEntry? CurrentEntry { get; set; }
}
