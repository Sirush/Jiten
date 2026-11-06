using FluentAssertions;
using Jiten.Core.Data.JMDict;
using Xunit;

namespace Jiten.Parser.Tests;

public class PitchAccentImporterTests
{
    private static JmDictWordForm Kanji(int wordId, short index, string text, string ruby)
        => new() { WordId = wordId, ReadingIndex = index, Text = text, RubyText = ruby, FormType = JmDictFormType.KanjiForm };

    private static JmDictWordForm Kana(int wordId, short index, string text)
        => new() { WordId = wordId, ReadingIndex = index, Text = text, RubyText = text, FormType = JmDictFormType.KanaForm };

    private static JmDictWord Word(int wordId, params JmDictWordForm[] forms)
        => new() { WordId = wordId, Forms = forms.ToList(), PitchAccents = [9] };

    [Fact]
    public void KanjiSpellingSharedWithAnotherReadingTakesTheAccentOfItsOwnReading()
    {
        var source = new PitchAccentSource();
        source.Add("彼の", "かの", [1]);
        source.Add("彼の", "あの", [0]);
        source.Add("あの", "あの", [0]);
        var word = Word(1, Kanji(1, 0, "彼の", "彼[あ]の"), Kana(1, 1, "あの"));

        PitchAccentImporter.Assign(word, source).Should().BeTrue();

        word.Forms.Select(f => f.PitchAccents).Should().AllBeEquivalentTo(new List<int> { 0 });
        word.PitchAccents.Should().Equal(0);
    }

    [Fact]
    public void KanaFormTakesTheKanjiFormsAccentOverAHomophoneKanaRecord()
    {
        var source = new PitchAccentSource();
        source.Add("雨", "あめ", [1]);
        source.Add("あめ", "あめ", [0]);
        var word = Word(2, Kanji(2, 0, "雨", "雨[あめ]"), Kana(2, 1, "あめ"));

        PitchAccentImporter.Assign(word, source);

        word.Forms.Single(f => f.ReadingIndex == 1).PitchAccents.Should().Equal(1);
    }

    [Fact]
    public void KanjiSpellingPairedWithAnotherReadingWinsOverAKanaRecord()
    {
        var source = new PitchAccentSource();
        source.Add("言う", "いう", [0]);
        source.Add("言う", "ゆう", [0]);
        source.Add("ゆう", "ゆう", [1]);
        var word = Word(9, Kanji(9, 0, "言う", "言[い]う"), Kana(9, 1, "いう"), Kana(9, 2, "ゆう"));

        PitchAccentImporter.Assign(word, source);

        word.Forms[2].PitchAccents.Should().Equal(0);
    }

    [Fact]
    public void EachReadingOfOneWordKeepsItsOwnAccent()
    {
        var source = new PitchAccentSource();
        source.Add("その", "その", [0]);
        source.Add("そん", "そん", [1]);
        var word = Word(3, Kana(3, 0, "その"), Kana(3, 1, "そん"));

        PitchAccentImporter.Assign(word, source);

        word.Forms[0].PitchAccents.Should().Equal(0);
        word.Forms[1].PitchAccents.Should().Equal(1);
        word.PitchAccents.Should().Equal(0);
    }

    [Fact]
    public void FormWithoutRecordIsMarkedEmptyAndStaleWordAccentIsCleared()
    {
        var source = new PitchAccentSource();
        source.Add("丈", "じょう", [0]);
        var word = Word(4, Kanji(4, 0, "丈", "丈[だけ]"), Kana(4, 1, "だけ"));

        PitchAccentImporter.Assign(word, source).Should().BeTrue();

        word.Forms.Should().OnlyContain(f => f.PitchAccents != null && f.PitchAccents.Count == 0);
        word.PitchAccents.Should().BeNull();
    }

    [Fact]
    public void KatakanaFormMatchesHiraganaRecordAndInheritsAcrossScripts()
    {
        var source = new PitchAccentSource();
        source.Add("奴", "やつ", [1]);
        var word = Word(5, Kanji(5, 0, "奴", "奴[やつ]"), Kana(5, 1, "ヤツ"));

        PitchAccentImporter.Assign(word, source);

        word.Forms[1].PitchAccents.Should().Equal(1);
    }

    [Fact]
    public void KanjiFormWithoutRubyMatchesAnyOfTheWordsReadings()
    {
        var source = new PitchAccentSource();
        source.Add("潜る", "もぐる", [2]);
        var word = Word(6, Kanji(6, 0, "潜る", "潜る"), Kana(6, 1, "もぐる"));

        PitchAccentImporter.Assign(word, source);

        word.Forms[0].PitchAccents.Should().Equal(2);
        word.Forms[1].PitchAccents.Should().Equal(2);
    }

    [Fact]
    public void ReadingNotationMarksAreIgnored()
    {
        var source = new PitchAccentSource();
        source.Add("お願い", "お∘ねがい", [0]);
        var word = Word(7, Kanji(7, 0, "お願い", "お願[ねが]い"), Kana(7, 1, "おねがい"));

        PitchAccentImporter.Assign(word, source);

        word.Forms[0].PitchAccents.Should().Equal(0);
    }

    [Fact]
    public void CustomWordWithoutAnyRecordIsLeftUntouched()
    {
        var word = Word(8000002, Kanji(8000002, 0, "逢魔", "逢[おう]魔[ま]"), Kana(8000002, 1, "おうま"));

        PitchAccentImporter.Assign(word, new PitchAccentSource()).Should().BeFalse();

        word.PitchAccents.Should().Equal(9);
        word.Forms.Should().OnlyContain(f => f.PitchAccents == null);
    }

    [Fact]
    public void ResolverPrefersTheFormAndFallsBackToTheWordUntilImported()
    {
        var word = new JmDictWord { PitchAccents = [3] };

        PitchAccentResolver.For(word, new JmDictWordForm { PitchAccents = [1] }).Should().Equal(1);
        PitchAccentResolver.For(word, new JmDictWordForm { PitchAccents = [] }).Should().BeEmpty();
        PitchAccentResolver.For(word, new JmDictWordForm()).Should().Equal(3);
        PitchAccentResolver.For(word, null).Should().Equal(3);
    }
}
