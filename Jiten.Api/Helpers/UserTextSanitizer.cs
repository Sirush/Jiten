using System.Globalization;
using System.Text;

namespace Jiten.Api.Helpers;

/// <summary>Cleans free text other users will see: control and invisible format characters go, stacked combining marks are capped and whitespace collapses.</summary>
public static class UserTextSanitizer
{
    /// <summary>Covers Vietnamese and emoji variation selectors; longer stacks only spill over neighbouring lines.</summary>
    private const int MaxMarksPerBase = 3;

    private const int MaxConsecutiveLineBreaks = 2;
    private const int ZeroWidthJoiner = 0x200D;

    /// <summary>Letters and symbols that render blank, used to fake an empty name.</summary>
    private static readonly HashSet<int> BlankLookalikes = [0x115F, 0x1160, 0x2800, 0x3164, 0xFFA0];

    /// <summary>Returns null when nothing visible is left. Line breaks survive only when <paramref name="multiline"/> is set.</summary>
    public static string? Clean(string? value, bool multiline)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var builder = new StringBuilder(value.Length);
        var marks = MaxMarksPerBase;
        var pendingSpace = false;
        var pendingBreaks = 0;
        var visible = false;

        foreach (var rune in value.Normalize(NormalizationForm.FormC).ReplaceLineEndings("\n").EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);

            if (rune.Value == '\n')
            {
                if (multiline) pendingBreaks++;
                else pendingSpace = true;
                marks = MaxMarksPerBase;
                continue;
            }

            if (Rune.IsWhiteSpace(rune) || BlankLookalikes.Contains(rune.Value))
            {
                pendingSpace = true;
                marks = MaxMarksPerBase;
                continue;
            }

            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                if (marks < MaxMarksPerBase)
                {
                    builder.Append(rune);
                    marks++;
                }
                continue;
            }

            if (category == UnicodeCategory.Format)
            {
                if (rune.Value == ZeroWidthJoiner && visible && !pendingSpace && pendingBreaks == 0)
                    builder.Append(rune);
                continue;
            }

            if (category == UnicodeCategory.Control)
                continue;

            if (visible)
            {
                if (pendingBreaks > 0) builder.Append('\n', Math.Min(pendingBreaks, MaxConsecutiveLineBreaks));
                else if (pendingSpace) builder.Append(' ');
            }

            builder.Append(rune);
            visible = true;
            marks = 0;
            pendingSpace = false;
            pendingBreaks = 0;
        }

        return visible ? builder.ToString() : null;
    }
}
