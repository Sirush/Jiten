using FluentAssertions;
using Jiten.Api.Helpers;

namespace Jiten.Tests;

public class ConfusableReadingsHelperTests
{
    [Theory]
    [InlineData("よそう", "よそおう")]
    [InlineData("よそおう", "よそう")]
    [InlineData("おおきい", "おおきい")]
    [InlineData("きょう", "きょおう")]
    [InlineData("かあ", "かああ")]
    [InlineData("らめん", "らーめん")]
    [InlineData("こと", "こうと")]
    [InlineData("せんせ", "せんせい")]
    [InlineData("こおり", "こうり")]
    [InlineData("ケイ", "け")]
    public void SpoilsOwnReading_HidesVowelLengthVariants(string confusable, string own)
    {
        ConfusableReadingsHelper.SpoilsOwnReading(confusable, [own]).Should().BeTrue();
    }

    [Theory]
    [InlineData("かた", "ほう")]
    [InlineData("よそう", "よそいう")]
    [InlineData("いく", "ゆく")]
    [InlineData("にん", "にんい")]
    [InlineData("こい", "こう")]
    [InlineData("かい", "か")]
    public void SpoilsOwnReading_KeepsDistinctReadings(string confusable, string own)
    {
        ConfusableReadingsHelper.SpoilsOwnReading(confusable, [own]).Should().BeFalse();
    }
}
