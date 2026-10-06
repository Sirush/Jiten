using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Diagnostics;
using Jiten.Parser.Romanization;

namespace Jiten.Tests;

public class TitleRomanizerTests
{
    [Theory]
    [InlineData("アフィリエイト", "afirieito")]
    [InlineData("ティーパーティー", "tiipaatii")]
    [InlineData("ファイル", "fairu")]
    [InlineData("ウェブ", "webu")]
    [InlineData("ヴァイオリン", "vaiorin")]
    [InlineData("チェック", "chekku")]
    [InlineData("エッチ", "etchi")]
    [InlineData("れんあい", "ren'ai")]
    [InlineData("きんよう", "kin'you")]
    [InlineData("しゅうまつ", "shuumatsu")]
    [InlineData("じょうおう", "jouou")]
    [InlineData("スーパー", "suupaa")]
    public void HepburnSpellsLoanwordsAndLongVowels(string kana, string expected)
    {
        HepburnRomanizer.ToRomaji(kana).Should().Be(expected);
    }

    [Fact]
    public void NumbersAndPunctuationSurviveTheParser()
    {
        var shiawase = Entry(1, ("幸せ", "幸[しあわ]せ"));
        var result = TitleRomanizer.Romanize("200％幸せ！『恋』",
            [Word(1, "幸せ", PartOfSpeech.Noun), Word(2, "恋", PartOfSpeech.Noun)],
            [Token("２００", "Noun", "ニレイレイ"), Token("％", "Noun", "パーセント"), Token("幸せ", "Noun", "シアワセ"),
             Token("！", "SupplementarySymbol", "!"), Token("『", "SupplementarySymbol", "キゴウ"),
             Token("恋", "Noun", "コイ"), Token("』", "SupplementarySymbol", "キゴウ")],
            Dictionary(shiawase, Entry(2, ("恋", "恋[こい]"))));

        result.Should().Be("200% Shiawase! \"Koi\"");
    }

    [Fact]
    public void DictionaryReadingBeatsSudachiReading()
    {
        var result = TitleRomanizer.Romanize("私の名前",
            [Word(1, "私", PartOfSpeech.Pronoun), Word(2, "の", PartOfSpeech.Particle), Word(3, "名前", PartOfSpeech.Noun)],
            [Token("私", "Pronoun", "ワタクシ"), Token("の", "Particle", "ノ"), Token("名前", "Noun", "ナマエ")],
            Dictionary(Entry(1, ("私", "私[わたし]")), Entry(3, ("名前", "名[な]前[まえ]"))));

        result.Should().Be("Watashi no Namae");
    }

    [Fact]
    public void MergedWordReadsFromEveryToken()
    {
        var result = TitleRomanizer.Romanize("最幸",
            [Word(1, "最幸", PartOfSpeech.Noun)],
            [Token("最", "Prefix", "サイ"), Token("幸", "Noun", "コウ")],
            Dictionary(Entry(1, ("最幸", "最[さい]幸[こう]"))));

        result.Should().Be("Saikou");
    }

    [Fact]
    public void TrailingCopulaSplitsFromItsNoun()
    {
        var result = TitleRomanizer.Romanize("幸せな愛",
            [Word(1, "幸せな", PartOfSpeech.Noun), Word(2, "愛", PartOfSpeech.Noun)],
            [Token("幸せ", "Noun", "シアワセ"), Token("な", "Auxiliary", "ナ"), Token("愛", "Noun", "アイ")],
            Dictionary(Entry(1, ("幸せ", "幸[しあわ]せ")), Entry(2, ("愛", "愛[あい]"))));

        result.Should().Be("Shiawase na Ai");
    }

    [Fact]
    public void DroppedTokenJoinsTheFollowingSuffix()
    {
        var result = TitleRomanizer.Romanize("子狐たちの災園",
            [Word(1, "子狐", PartOfSpeech.Noun), Word(2, "たち", PartOfSpeech.Suffix), Word(3, "の", PartOfSpeech.Particle),
             Word(4, "園", PartOfSpeech.NounSuffix)],
            [Token("子狐", "Noun", "コギツネ"), Token("たち", "Suffix", "タチ"), Token("の", "Particle", "ノ"),
             Token("災", "Noun", "サイ"), Token("園", "Suffix", "エン")],
            Dictionary(Entry(1, ("子狐", "子[こ]狐[ぎつね]")), Entry(4, ("園", "園[えん]"))));

        result.Should().Be("Kogitsune-tachi no Saien");
    }

    [Fact]
    public void LatinTextIsKeptAndSuffixHyphenates()
    {
        var result = TitleRomanizer.Romanize("Web屋",
            [Word(1, "屋", PartOfSpeech.Suffix)],
            [Token("Web", "Noun", "ウェブ"), Token("屋", "Suffix", "ヤ")],
            Dictionary(Entry(1, ("屋", "屋[や]"))));

        result.Should().Be("Web-ya");
    }

    [Fact]
    public void TitleHeldAsOneNameSplitsAtParticles()
    {
        var result = TitleRomanizer.Romanize("君の名は。",
            [Word(1, "君の名は", PartOfSpeech.Name)],
            [Token("君の名は", "Noun", "キミノナハ"), Token("。", "SupplementarySymbol", "。")],
            Dictionary(Entry(1, ("君の名は", "君の名は"))));

        result.Should().Be("Kimi no Na wa.");
    }

    [Fact]
    public void PairedWaveDashesWrapTheSubtitle()
    {
        var result = TitleRomanizer.Romanize("恋～天才～",
            [Word(1, "恋", PartOfSpeech.Noun), Word(2, "天才", PartOfSpeech.Noun)],
            [Token("恋", "Noun", "コイ"), Token("天才", "Noun", "テンサイ")],
            Dictionary(Entry(1, ("恋", "恋[こい]")), Entry(2, ("天才", "天[てん]才[さい]"))));

        result.Should().Be("Koi ~Tensai~");
    }

    private static DeckWord Word(int wordId, string surface, PartOfSpeech pos) =>
        new() { WordId = wordId, OriginalText = surface, PartsOfSpeech = [pos] };

    private static SudachiToken Token(string surface, string pos, string reading) =>
        new() { Surface = surface, PartOfSpeech = pos, Reading = reading };

    private static JmDictWord Entry(int wordId, params (string Text, string Ruby)[] forms) =>
        new()
        {
            WordId = wordId,
            Forms = forms.Select((f, i) => new JmDictWordForm
            {
                WordId = wordId, ReadingIndex = (short)i, Text = f.Text, RubyText = f.Ruby,
                FormType = f.Ruby.Contains('[') ? JmDictFormType.KanjiForm : JmDictFormType.KanaForm
            }).ToList()
        };

    private static Dictionary<int, JmDictWord> Dictionary(params JmDictWord[] entries) => entries.ToDictionary(e => e.WordId);
}
