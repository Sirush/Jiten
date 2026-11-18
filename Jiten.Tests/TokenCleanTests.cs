using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Jiten.Tests;

public class TokenCleanTests
{
    private static readonly Regex ReferenceTokenCleanRegex = new(
        @"[^a-zA-Z0-9぀-ゟ゠-ヿ一-龯Ａ-Ｚａ-ｚ０-９々．]");

    [Fact]
    public void IsTokenCharMatchesReferenceRegexForEveryCodeUnit()
    {
        var mismatches = new List<string>();
        for (int i = 0; i <= 0xFFFF; i++)
        {
            var c = (char)i;
            bool regexKeeps = ReferenceTokenCleanRegex.Replace(c.ToString(), "").Length == 1;
            if (Parser.Parser.IsTokenChar(c) != regexKeeps)
                mismatches.Add($"U+{i:X4}");
        }

        mismatches.Should().BeEmpty(string.Join(",", mismatches.Take(40)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("食べる")]
    [InlineData("「食べる」")]
    [InlineData("…ッー！")]
    [InlineData("ＡＢＣ１２３．々abcXYZ09")]
    [InlineData("、。！？・")]
    [InlineData("a😀b")]
    public void StripNonTokenCharsMatchesReferenceRegex(string text)
    {
        Parser.Parser.StripNonTokenChars(text).Should().Be(ReferenceTokenCleanRegex.Replace(text, ""));
    }

    [Fact]
    public void StripNonTokenCharsMatchesReferenceRegexOnLongText()
    {
        var text = string.Concat(Enumerable.Repeat("「あ、い」ＡＢ１!?漢字ー", 40));
        Parser.Parser.StripNonTokenChars(text).Should().Be(ReferenceTokenCleanRegex.Replace(text, ""));
    }
}
