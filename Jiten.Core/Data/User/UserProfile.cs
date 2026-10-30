namespace Jiten.Core.Data;

public class UserProfile
{
    public string UserId { get; set; } = string.Empty;

    public const int MaxBioLength = 200;

    /// <summary>Pre-privacy-levels profile flag, mirrored on save; only read as the default profile level while <see cref="PrivacyJson"/> is empty.</summary>
    public bool IsPublic { get; set; } = false;

    /// <summary>Pre-privacy-levels media list flag; only read as the default for the Library section while <see cref="PrivacyJson"/> is empty.</summary>
    public bool IsMediaListPublic { get; set; } = false;

    public string? Bio { get; set; }

    public string LinksJson { get; set; } = "[]";

    /// <summary>A single kanji or kana drawn as the avatar; null falls back to the display name's first character.</summary>
    public string? AvatarKanji { get; set; }

    /// <summary>Key into the fixed profile palette, never a raw colour.</summary>
    public string? AvatarColour { get; set; }

    public const string DefaultAvatarStyle = "filled";
    public const string StrokeAvatarStyle = "stroke";

    /// <summary>How the frontend draws the avatar character; anything else reads as <see cref="DefaultAvatarStyle"/>.</summary>
    public static readonly IReadOnlySet<string> AvatarStyles = new HashSet<string> { DefaultAvatarStyle, "seal", "paper", "crest", StrokeAvatarStyle, "brush" };

    /// <summary>One of <see cref="AvatarStyles"/>; null is <see cref="DefaultAvatarStyle"/>.</summary>
    public string? AvatarStyle { get; set; }

    public static string ResolveAvatarStyle(string? style) => style != null && AvatarStyles.Contains(style) ? style : DefaultAvatarStyle;

    /// <summary>Key into the fixed profile palette, never a raw colour.</summary>
    public string? HeaderColour { get; set; }

    /// <summary>Serialised <see cref="ProfilePrivacy"/>; "{}" means every setting is at its default.</summary>
    public string PrivacyJson { get; set; } = "{}";
}
