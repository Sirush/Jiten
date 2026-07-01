using System.Text.RegularExpressions;

namespace Jiten.Api.Helpers;

/// <summary>
/// Central username validation shared by the email/password and Google registration paths so both
/// reject the same inputs with the same messages.
/// The allowed set is a subset of ASP.NET Identity's default <c>AllowedUserNameCharacters</c>, so a
/// name that passes here is always accepted by <c>UserManager.CreateAsync</c>.
/// </summary>
public static partial class UsernameValidator
{
    public const int MinLength = 2;
    public const int MaxLength = 30;

    // Latin letters/digits plus the punctuation Identity's default AllowedUserNameCharacters permits
    // (email-style names like tony@aol.com are allowed). This set is a subset of that default, so a
    // name accepted here always passes UserManager.CreateAsync.
    [GeneratedRegex(@"^[A-Za-z0-9._@+-]+$")]
    private static partial Regex AllowedPattern();

    /// <summary>
    /// Validates a username. Returns null when valid, otherwise a user-facing error message.
    /// Callers should pass the already-trimmed username.
    /// </summary>
    public static string? Validate(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "Username is required.";

        if (username.Length < MinLength)
            return $"Username must be at least {MinLength} characters.";

        if (username.Length > MaxLength)
            return $"Username must be at most {MaxLength} characters.";

        if (!AllowedPattern().IsMatch(username))
            return "Username can only contain Latin letters, digits and the characters . _ - @ +";

        if (!username.Any(char.IsAsciiLetterOrDigit))
            return "Username must contain at least one letter or digit.";

        return null;
    }
}
