namespace Jiten.Core.Data;

/// <summary>One row per user holding client-side preference documents, one JSON column per settings domain.</summary>
public class UserSettings
{
    public string UserId { get; set; } = string.Empty;

    public string MediaFilterPresetsJson { get; set; } = "{}";
}
