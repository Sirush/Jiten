using System.Text;
using System.Text.RegularExpressions;

namespace Jiten.Api.Helpers;

public static partial class DiscordMarkdown
{
    [GeneratedRegex(@"(https?:\/\/[^\s)]+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>Escapes Discord markdown and mentions in user text while leaving URLs clickable.</summary>
    public static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return "";

        var sb = new StringBuilder();
        int lastIndex = 0;

        foreach (Match match in UrlRegex().Matches(input))
        {
            if (match.Index > lastIndex)
                AppendEscaped(sb, input.AsSpan(lastIndex, match.Index - lastIndex));

            sb.Append(match.Value);
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < input.Length)
            AppendEscaped(sb, input.AsSpan(lastIndex));

        return sb.ToString();
    }

    private static void AppendEscaped(StringBuilder sb, ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (c is '*' or '_' or '~' or '`' or '>' or '|' or '[' or ']' or '(' or ')' or '@' or '#' or ':' or '"')
                sb.Append('\\');

            sb.Append(c);
        }
    }
}
