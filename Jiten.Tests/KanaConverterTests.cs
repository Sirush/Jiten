using FluentAssertions;
using Jiten.Core;
using Jiten.Parser;
using Xunit;

namespace Jiten.Tests;

public class KanaConverterTests
{
    // The fast path in KanaConverter.ToHiragana returns the input untouched for plain hiragana +
    // kanji. This pins the assumption it rests on: WanaKana leaves every such string unchanged.
    [Fact]
    public void PlainHiraganaAndKanjiPassThroughWanaKanaUnchanged()
    {
        var samples = new List<string>();
        for (char c = 'ぁ'; c <= 'ゔ'; c++)
        {
            if (c == 'ゎ') continue;
            samples.Add(c.ToString());
            samples.Add("食" + c + "る");
            samples.Add(c + "ん" + c);
        }

        samples.AddRange(["漢字", "々", "食べている", "ばっかり", "っ", "ん", "ゐゑ", "ゔぁ", "行っちゃった"]);

        foreach (var s in samples)
        {
            JapaneseTextHelper.ToHiragana(s, convertLongVowelMark: true).Should().Be(s, because: $"'{s}' with long-vowel conversion");
            JapaneseTextHelper.ToHiragana(s, convertLongVowelMark: false).Should().Be(s, because: $"'{s}' without long-vowel conversion");
            KanaConverter.ToHiragana(s).Should().Be(s);
            KanaConverter.ToHiragana(s, convertLongVowelMark: false).Should().Be(s);
        }
    }

    // The katakana fast path must agree with WanaKana (via JapaneseTextHelper) wherever it claims
    // a string: every char in the kana blocks alone and in pairs, with kanji and ー mixed in.
    [Fact]
    public void KatakanaFoldMatchesWanaKanaWhereverItApplies()
    {
        var mismatches = new List<string>();
        void Check(string s)
        {
            foreach (var lvm in new[] { true, false })
            {
                if (!KanaConverter.TryFoldKatakana(s, lvm, out var folded)) continue;
                var expected = JapaneseTextHelper.ToHiragana(s, lvm);
                if (folded != expected)
                    mismatches.Add($"{s}/{lvm}: {folded} vs {expected}");
            }
        }

        for (int i = 0x3040; i <= 0x30FF; i++)
        {
            var c = (char)i;
            Check(c.ToString());
            Check("食" + c);
            Check(c + "ー");
            for (int j = 0x3040; j <= 0x30FF; j++)
                Check(new string([c, (char)j]));
        }

        mismatches.Should().BeEmpty(string.Join(" | ", mismatches.Take(30)));
    }

    [Theory]
    [InlineData("テスト", "てすと")]
    [InlineData("ラーメン", "らあめん")]
    [InlineData("ヶ月", "け月")]
    [InlineData("ゎ", "わ")]
    public void ConvertedSurfacesStillGoThroughWanaKana(string input, string expected)
    {
        KanaConverter.ToHiragana(input).Should().Be(expected);
    }
}
