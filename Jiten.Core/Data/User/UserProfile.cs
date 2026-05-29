namespace Jiten.Core.Data;

public class UserProfile
{
    public string UserId { get; set; } = string.Empty;

    public bool IsPublic { get; set; } = false;

    /// <summary>
    /// Whether the user's tracked media list (and the vocab-list downloads built from it) is visible to others.
    /// Independent of <see cref="IsPublic"/>.
    /// </summary>
    public bool IsMediaListPublic { get; set; } = false;
}
