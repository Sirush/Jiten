namespace Jiten.Core.Data;

public class UserDeckPreference
{
    public string UserId { get; set; } = null!;
    public int DeckId { get; set; }
    public DeckStatus Status { get; set; }
    public bool IsFavourite { get; set; }
    public bool IsIgnored { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>The entry that status changes act on; past entries added by hand never take this over.</summary>
    public long? CurrentEntryId { get; set; }
    public UserMediaListEntry? CurrentEntry { get; set; }
}
