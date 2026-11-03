namespace Jiten.Core.Data.Authentication;
public enum UserRole
{
    User = 1,
    Administrator = 2,
    /// <summary>Approves, rejects and reverts community metadata edits at every risk tier.</summary>
    Editor = 3,
    /// <summary>Approves and rejects low-risk community metadata edits and reverts live ones.</summary>
    Reviewer = 4
}