using Jiten.Core.Data.JMDict;
using Jiten.Parser.Scoring;

namespace Jiten.Tests;

using Xunit;
using FluentAssertions;

public class FuriganaHintScorerTests
{
    private static (JmDictWord word, JmDictWordForm kanjiForm) CreateWord(
        int wordId, string kanjiText, string kanaReading, byte readingIndex = 0)
    {
        var kanjiForm = new JmDictWordForm
        {
            WordId = wordId,
            ReadingIndex = readingIndex,
            Text = kanjiText,
            FormType = JmDictFormType.KanjiForm
        };
        var kanaForm = new JmDictWordForm
        {
            WordId = wordId,
            ReadingIndex = readingIndex,
            Text = kanaReading,
            FormType = JmDictFormType.KanaForm
        };
        var word = new JmDictWord
        {
            WordId = wordId,
            PartsOfSpeech = ["n"],
            Priorities = [],
            Forms = [kanjiForm, kanaForm]
        };
        return (word, kanjiForm);
    }

    [Fact]
    public void Score_NullHint_ReturnsZero()
    {
        var (word, form) = CreateWord(1000, "漢字", "かんじ");
        var candidate = new FormCandidate(word, form, 0, "かんじ");

        FuriganaHintScorer.Score(candidate, null).Should().Be(0);
    }

    [Fact]
    public void Score_MatchingReading_Returns500()
    {
        var (word, form) = CreateWord(1000, "漢字", "かんじ");
        var candidate = new FormCandidate(word, form, 0, "かんじ");

        FuriganaHintScorer.Score(candidate, "かんじ").Should().Be(500);
    }

    [Fact]
    public void Score_NonMatchingReading_ReturnsZero()
    {
        var (word, form) = CreateWord(1000, "漢字", "かんじ");
        var candidate = new FormCandidate(word, form, 0, "かんじ");

        FuriganaHintScorer.Score(candidate, "ほし").Should().Be(0);
    }

    [Fact]
    public void Score_KatakanaHint_MatchesHiraganaReading()
    {
        var (word, form) = CreateWord(1000, "日", "にち");
        var candidate = new FormCandidate(word, form, 0, "にち");
        var hintHiragana = KanaScoringHelpers.ToNormalizedHiragana("ニチ", convertLongVowelMark: false);

        FuriganaHintScorer.Score(candidate, hintHiragana).Should().Be(500);
    }

    [Fact]
    public void Score_MultipleReadings_BoostsAllCandidatesWhenWordHasMatchingReading()
    {
        var kanjiForm = new JmDictWordForm
        {
            WordId = 2000, ReadingIndex = 0, Text = "日",
            FormType = JmDictFormType.KanjiForm
        };
        var kanaForm0 = new JmDictWordForm
        {
            WordId = 2000, ReadingIndex = 1, Text = "にち",
            FormType = JmDictFormType.KanaForm
        };
        var kanaForm1 = new JmDictWordForm
        {
            WordId = 2000, ReadingIndex = 2, Text = "ひ",
            FormType = JmDictFormType.KanaForm
        };
        var word = new JmDictWord
        {
            WordId = 2000,
            PartsOfSpeech = ["n"],
            Priorities = [],
            Forms = [kanjiForm, kanaForm0, kanaForm1]
        };

        var candidate0 = new FormCandidate(word, kanjiForm, 0, "にち");

        FuriganaHintScorer.Score(candidate0, "ひ").Should().Be(500);
        FuriganaHintScorer.Score(candidate0, "にち").Should().Be(500);
        FuriganaHintScorer.Score(candidate0, "か").Should().Be(0);
    }

    [Theory]
    [InlineData("一緒", "いっしょ", "いつしよ")]
    [InlineData("状況", "じょうきょう", "じようきよう")]
    [InlineData("格好", "かっこう", "かつこう")]
    public void Score_HintTypesetWithoutSmallKana_MatchesSmallKanaReading(string kanji, string reading, string hint)
    {
        var (word, form) = CreateWord(1000, kanji, reading);
        var candidate = new FormCandidate(word, form, 0, reading);

        FuriganaHintScorer.Score(candidate, hint).Should().Be(500);
    }

    [Fact]
    public void Score_HintWithSmallKana_DoesNotMatchLargeKanaReading()
    {
        var (word, form) = CreateWord(1000, "器用", "きよう");
        var candidate = new FormCandidate(word, form, 0, "きよう");

        FuriganaHintScorer.Score(candidate, "きょう").Should().Be(0);
    }

    [Fact]
    public void Score_HintMixingSmallAndLargeKana_RequiresExactMatch()
    {
        var (word, form) = CreateWord(1000, "出張", "しゅっちょう");
        var candidate = new FormCandidate(word, form, 0, "しゅっちょう");

        FuriganaHintScorer.Score(candidate, "しゅつちょう").Should().Be(0);
    }
}
