using System.Text;
using System.Text.RegularExpressions;

namespace Jiten.Api.Helpers;

/// <summary>
/// Rules for the public display name. The name doubles as the initial forum username, so the format
/// mirrors Discourse's UsernameValidator with <c>unicode_usernames</c> on and <c>min_username_length</c> lowered to 2
/// for two-kanji names.
/// </summary>
public static partial class DisplayNameValidator
{
    public const int MinLength = 2;
    public const int MaxLength = 20;

    public const string UnavailableMessage = "This display name isn't available.";

    // Impersonation of the site, its staff or the forum. Matched anywhere in the name.
    private static readonly string[] ReservedSubstrings =
    [
        "jiten", "admin", "system", "moderator", "discourse", "official", "sirus",
        "じてん", "辞典", "字典", "管理人", "管理者", "運営", "公式", "あどみん", "しすてむ", "もでれーたー"
    ];

    // Matched as a whole word, so "Mod_Tanaka" is refused but "Modern" is not.
    private static readonly string[] ReservedTokens = ["mod", "mods", "staff", "support", "bot", "root", "owner", "sys", "sysop"];

    // Discourse group mentions, reserved usernames and placeholder names. Matched against the whole name only.
    private static readonly string[] ReservedExact =
    [
        "everyone", "here", "all", "admins", "moderators", "trustlevel0", "trustlevel1", "trustlevel2", "trustlevel3",
        "trustlevel4", "discobot", "null", "undefined", "anonymous", "anon", "deleted", "deleteduser", "guest", "user", "users",
        "username", "name", "nickname", "you", "me", "info", "community", "forum", "forums", "help", "contact", "security",
        "abuse", "postmaster", "webmaster", "noreply", "api", "www"
    ];

    private static readonly string[] OffensiveSubstrings =
    [
        "nigger", "nigga", "faggot", "retard", "tranny", "hitler", "nazi", "siegheil", "whore", "slut", "bitch", "fuck", "cunt",
        "porn", "rapist", "lolicon", "shotacon", "pedophil", "paedophil", "penis", "vagina",
        "ちんこ", "ちんぽ", "まんこ", "れいぷ", "せっくす", "死ね", "殺す"
    ];

    // Short words that collide with ordinary words and romaji ("Yamashita", "kiken", "torpedo") when matched as substrings.
    private static readonly string[] OffensiveTokens =
    [
        "fag", "fags", "kike", "spic", "chink", "coon", "rape", "cock", "dick", "cum", "anal", "pedo", "loli", "shit", "sex",
        "kys", "kkk", "twat", "dyke", "nsfw"
    ];

    // Legitimate words that contain a reserved or offensive substring; removed before substring matching.
    private static readonly string[] AllowedWords = ["jitensha", "じてんしゃ", "badminton", "ecosystem", "scunthorpe"];

    private static readonly Regex ConfusingExtension =
        new(@"\.(js|json|css|htm|html|xml|jpg|jpeg|png|gif|bmp|ico|tif|tiff|woff)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex("[-_.]{2,}")]
    private static partial Regex RepeatedSeparator();

    [GeneratedRegex("[-_.]")]
    private static partial Regex Separator();

    [GeneratedRegex(@"(?<=\p{Ll})(?=\p{Lu})|(?<=\p{L})(?=\d)|(?<=\d)(?=\p{L})")]
    private static partial Regex WordBoundary();

    [GeneratedRegex(@"(.)\1+")]
    private static partial Regex RepeatedChar();

    /// <summary>Canonical stored form: trimmed and NFKC, so full-width Latin and half-width kana fold to their usual forms.</summary>
    public static string Normalize(string? displayName) => (displayName ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC);

    /// <summary>Case-insensitive uniqueness key for an already normalised name.</summary>
    public static string ToKey(string normalizedName) => normalizedName.ToUpperInvariant();

    /// <summary>
    /// Returns null when valid, otherwise a user-facing error. Expects a name from <see cref="Normalize"/>.
    /// Staff may use reserved names (the site's own accounts), never offensive ones.
    /// </summary>
    public static string? Validate(string name, bool allowReserved = false)
    {
        if (name.Length == 0)
            return "Display name is required.";

        if (name.Length < MinLength)
            return $"Display name must be at least {MinLength} characters.";

        if (name.Length > MaxLength)
            return $"Display name must be at most {MaxLength} characters.";

        if (!name.All(c => IsNameLetter(c) || c is '_' or '.' or '-'))
            return "Display name can only contain letters, digits, Japanese characters and the characters . _ -";

        if (!IsNameLetter(name[0]) && name[0] != '_')
            return "Display name must start with a letter, digit or underscore.";

        if (!IsNameLetter(name[^1]))
            return "Display name must end with a letter or digit.";

        if (RepeatedSeparator().IsMatch(name))
            return "Display name can't contain two of . _ - in a row.";

        if (ConfusingExtension.IsMatch(name))
            return "Display name can't end with a file extension.";

        if (ContainsOffensiveWord(name) || (!allowReserved && ContainsReservedWord(name)))
            return UnavailableMessage;

        return null;
    }

    public static bool ContainsReservedWord(string name) =>
        Matches(name, ReservedSubstrings, ReservedTokens) || MatchForms(name).Any(f => ReservedExact.Contains(f.Compact));

    public static bool ContainsOffensiveWord(string name) => Matches(name, OffensiveSubstrings, OffensiveTokens);

    private static bool Matches(string name, string[] substrings, string[] tokens)
    {
        foreach (var form in MatchForms(name))
        {
            var stripped = AllowedWords.Aggregate(form.Compact, (s, w) => s.Replace(w, string.Empty, StringComparison.Ordinal));
            if (substrings.Any(w => stripped.Contains(w, StringComparison.Ordinal)))
                return true;

            if (form.Tokens.Any(t => tokens.Contains(t)) || tokens.Contains(form.Compact))
                return true;
        }

        return false;
    }

    private readonly record struct MatchForm(string Compact, string[] Tokens);

    // Every spelling a filter evasion could hide behind: leetspeak digits (1 read as both i and l), letters
    // doubled to break a word, katakana written for hiragana, and separators placed inside a word.
    private static IEnumerable<MatchForm> MatchForms(string name)
    {
        var spellings = new[] { 'i', 'l' }
                        .Select(one => new string(name.Select(c => c switch
                        {
                            '0' => 'o', '1' => one, '3' => 'e', '4' => 'a', '5' => 's', '7' => 't', '8' => 'b', _ => c
                        }).ToArray()))
                        .Prepend(name);

        foreach (var spelling in spellings)
        {

            var tokens = Separator().Split(spelling)
                                    .SelectMany(part => WordBoundary().Split(part))
                                    .Where(t => t.Length > 0)
                                    .Select(Fold)
                                    .ToArray();
            var compact = Fold(Separator().Replace(spelling, string.Empty));

            yield return new MatchForm(compact, tokens);
            yield return new MatchForm(Collapse(compact), tokens.Select(Collapse).ToArray());
        }
    }

    private static string Fold(string s)
    {
        var chars = s.ToLowerInvariant().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] is >= 'ァ' and <= 'ヶ')
                chars[i] = (char)(chars[i] - 0x60);
        return new string(chars);
    }

    private static string Collapse(string s) => RepeatedChar().Replace(s, "$1");

    // Latin is ASCII-only so look-alike letters from other scripts (Cyrillic а, Greek ο) can't impersonate a name.
    private static bool IsNameLetter(char c) =>
        char.IsAsciiLetterOrDigit(c)
        || c is >= 'ぁ' and <= 'ゖ' // hiragana
        || c is 'ゝ' or 'ゞ' // ゝゞ
        || c is >= 'ァ' and <= 'ヺ' // katakana
        || c is >= 'ー' and <= 'ヾ' // ー ヽヾ
        || c is '々' or '〆' // 々〆
        || c is >= '㐀' and <= '䶿'
        || c is >= '一' and <= '鿿';
}
