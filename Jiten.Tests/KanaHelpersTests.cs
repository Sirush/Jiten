using FluentAssertions;
using Jiten.Core;
using WanaKanaShaapu;
using Xunit;

namespace Jiten.Tests;

public class KanaHelpersTests
{
    // IsAllKana replaces WanaKana.IsKana on the parse hot path; it must agree on every BMP char.
    [Fact]
    public void IsAllKanaMatchesWanaKanaForEveryChar()
    {
        var mismatches = new List<string>();
        for (int i = 0; i <= 0xFFFF; i++)
        {
            char c = (char)i;
            if (char.IsSurrogate(c)) continue;
            var s = c.ToString();
            if (WanaKana.IsKana(s) != JapaneseTextHelper.IsAllKana(s))
                mismatches.Add($"U+{i:X4}");
        }

        mismatches.Should().BeEmpty(string.Join(",", mismatches));
    }

    // HiraganaToKatakana replaces WanaKana.ToKatakana for kana-only surfaces of up to three chars;
    // single chars and every pair in the kana blocks must agree.
    [Fact]
    public void HiraganaToKatakanaMatchesWanaKanaOnKanaInput()
    {
        var mismatches = new List<string>();
        for (int i = 0x3040; i <= 0x30FF; i++)
        {
            var single = ((char)i).ToString();
            if (WanaKana.ToKatakana(single) != JapaneseTextHelper.HiraganaToKatakana(single))
                mismatches.Add(single);
            for (int j = 0x3040; j <= 0x30FF; j++)
            {
                var pair = new string([(char)i, (char)j]);
                if (WanaKana.ToKatakana(pair) != JapaneseTextHelper.HiraganaToKatakana(pair))
                    mismatches.Add(pair);
            }
        }

        mismatches.Should().BeEmpty(string.Join(",", mismatches.Take(40)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("かな")]
    [InlineData("カナ")]
    [InlineData("かナー")]
    [InlineData("か・な")]
    [InlineData("かn")]
    [InlineData("漢字")]
    [InlineData("ゝゞ")]
    [InlineData("ｶﾅ")]
    public void IsAllKanaMatchesWanaKanaForStrings(string s)
    {
        JapaneseTextHelper.IsAllKana(s).Should().Be(WanaKana.IsKana(s));
    }
}
