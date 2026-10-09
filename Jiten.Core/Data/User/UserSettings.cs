namespace Jiten.Core.Data;

/// <summary>One row per user holding client-side preference documents, one JSON column per settings domain.</summary>
public class UserSettings
{
    public string UserId { get; set; } = string.Empty;

    public string MediaFilterPresetsJson { get; set; } = "{}";

    public string SmartDeckJson { get; set; } = "{}";

    public string DisplayProfilesJson { get; set; } = "{}";

    /// <summary>JSON array of one-time notice keys the user has dismissed.</summary>
    public string DismissedNoticesJson { get; set; } = "[]";

    /// <summary>JSON array of granted notice keys, written by the migration that snapshots who a change affected at deploy time.</summary>
    public string GrantedNoticesJson { get; set; } = "[]";
}
